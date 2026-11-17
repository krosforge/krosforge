using System.Text;
using System.Text.Json;
using Krosforge.Contracts;
namespace Krosforge.Runtime;

public sealed record CopyProgress(long BytesCopied, long TotalBytes);
public sealed record PreparedClient(Guid ProfileId, string Build, string Company, string Product);
public static class ClientPreparation
{
    public static async Task<string> PrepareAsync(string source, string dataRoot, Profile profile, IProgress<CopyProgress>? progress, CancellationToken token)
    {
        var installation = ClientInstallation.Inspect(source);
        var target = Path.Combine(Path.GetFullPath(dataRoot), "clients", profile.Id.ToString("N"));
        EnsureDisjoint(installation.Path, target);
        SafePaths.CheckTree(target);
        if (Directory.Exists(target))
            throw new IOException("A cópia do perfil já existe.");
        if (profile.Slot is < 1 or > 65535)
            throw new InvalidOperationException("Identidade de perfil fora do intervalo suportado.");
        var files = EnumerateFiles(installation.Path).ToArray();
        var total = files.Sum(p => new FileInfo(p).Length);
        var drive = new DriveInfo(Path.GetPathRoot(target)!);
        if (drive.AvailableFreeSpace < total + 64L * 1024 * 1024)
            throw new IOException("Não há espaço suficiente para preparar a cópia.");
        Directory.CreateDirectory(target);
        long copied = 0;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, Path.GetRelativePath(installation.Path, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            var buffer = new byte[128 * 1024];
            int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), token);
                copied += count;
                progress?.Report(new(copied, total));
            }
        }
        token.ThrowIfCancellationRequested();
        ClientCompatibility.Validate(installation.Path);
        ClientCompatibility.Validate(target);
        var info = Path.Combine(target, "Dofus_Data", "app.info");
        var identity = await File.ReadAllLinesAsync(info, token);
        if (identity.Length != 2)
            throw new IOException("Identidade do cliente inválida.");
        var product = "P" + profile.Slot.ToString("X4");
        const string company = "Krscli";
        var settings = Path.Combine(target, "Dofus_Data", "globalgamemanagers");
        var bytes = await File.ReadAllBytesAsync(settings, token);
        var original = Serialized(identity[0]).Concat(Serialized(identity[1])).ToArray();
        var replacement = Serialized(company).Concat(Serialized(product)).ToArray();
        ReplaceUnique(bytes, original, replacement);
        await File.WriteAllBytesAsync(settings, bytes, token);
        await File.WriteAllTextAsync(info, company + "\n" + product + "\n", new UTF8Encoding(false), token);
        token.ThrowIfCancellationRequested();
        await File.WriteAllTextAsync(Path.Combine(target, "krosforge-client.json"), JsonSerializer.Serialize(new PreparedClient(profile.Id, installation.Build, company, product)), token);
        return target;
    }
    public static void EnsureDisjoint(string source, string target)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        if (source.Equals(target, StringComparison.OrdinalIgnoreCase) || source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Origem e destino precisam ser pastas independentes.");
    }
    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("A instalação contém links.");
            if ((attributes & FileAttributes.Directory) != 0)
                pending.Push(entry);
            else
                yield return entry;
        }
    }
    private static byte[] Serialized(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return BitConverter.GetBytes(bytes.Length).Concat(bytes).Concat(new byte[(4 - bytes.Length % 4) % 4]).ToArray();
    }
    public static void ReplaceUnique(byte[] data, byte[] expected, byte[] replacement)
    {
        if (expected.Length == 0 || expected.Length != replacement.Length)
            throw new IOException("Identidade serializada incompatível.");
        var found = -1;
        for (var i = 0; i <= data.Length - expected.Length; i++)
        if (data.AsSpan(i, expected.Length).SequenceEqual(expected))
        {
            if (found >= 0)
                throw new IOException("Identidade serializada ambígua.");
            found = i;
        }
        if (found < 0)
            throw new IOException("Identidade serializada ausente.");
        replacement.CopyTo(data, found);
    }
}
