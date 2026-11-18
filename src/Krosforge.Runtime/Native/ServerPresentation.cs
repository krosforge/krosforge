using System.Security.Cryptography;
using System.Text;
using Krosforge.Core;
namespace Krosforge.Runtime.Native;

// Interoperability identifiers and original file digests for build 3.7.14.18.
internal static class ServerPresentation
{
    internal const int RealmId = 290;
    internal const int NameKey = 980596;
    private static readonly Dictionary<string, string> Hashes = new()
    {
        ["fr"] = "40fb0c50526fdf084b2b8e16fd32f874a7ed633edc430f821f050b70dc7bb0ad",
        ["en"] = "f715f83a2afd2dcf3a22e65289a57a1c4d4aeb759caee3b29f4a576fa43767f0",
        ["de"] = "f723a00950daaa78c431a0ba9d347eba7bb58b9d76d4d1ccc6e2694c75325fbb",
        ["es"] = "71657e5f498d061ccbd75ba30b2a544e219c899e81c2f4379a50fa1c3303913c",
        ["pt"] = "39fa47ae696bf057e9681e61eeefce530a24bbc55dfd127f0e01bc367389d57c"
    };
    internal static async Task ApplyAsync(string source, string target, string name, CancellationToken token)
    {
        var relative = Path.Combine("Dofus_Data", "StreamingAssets", "Content", "Data", "data_assets_serversdataroot.asset.bundle");
        foreach (var root in new[] { source, target })
        {
            var catalog = Path.Combine(root, relative);
            Check(catalog);
            using var stream = File.OpenRead(catalog);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)) != "cdc11dcd10bfb373460a77d4ef8da15163e8cbf2fbfeba474dc85e35c0ea9dde")
                throw new IOException("ErrorServerPresentation");
        }
        await ApplyValidatedAsync(source, target, name, Hashes, token);
    }

    // Each retry derives the same bytes from the original. No native process may start
    // until all languages are verified; a partial promotion is repaired on retry.
    internal static async Task ApplyValidatedAsync(string source, string target, string name,
        IReadOnlyDictionary<string, string> hashes, CancellationToken token)
    {
        name = ServerName.Normalize(name);
        ClientPreparation.EnsureDisjoint(source, target);
        foreach (var (language, hash) in hashes)
        {
            token.ThrowIfCancellationRequested();
            if (language is not ("fr" or "en" or "de" or "es" or "pt")) throw new IOException("ErrorServerPresentation");
            var relative = Path.Combine("Dofus_Data", "StreamingAssets", "Content", "I18n", language + ".bin");
            var original = Path.Combine(source, relative);
            var destination = Path.Combine(target, relative);
            Check(original); Check(destination);
            var bytes = await File.ReadAllBytesAsync(original, token);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash) throw new IOException("ErrorServerPresentation");
            var expected = Rename(bytes, name);
            var current = await File.ReadAllBytesAsync(destination, token);
            if (current.AsSpan().SequenceEqual(expected)) continue;
            await ReplaceAsync(destination, expected, token);
        }
        // Informational completion record; never trusted instead of validating files.
        await ReplaceAsync(Path.Combine(target, "krosforge-server.json"),
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Build = ClientInstallation.SupportedBuild, Name = name }), token);
    }
    private static void Check(string path)
    {
        SafePaths.CheckTree(Path.GetDirectoryName(path)!);
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("ErrorCopyInvalid");
        }
        catch (FileNotFoundException) { }
    }
    private static async Task ReplaceAsync(string destination, byte[] bytes, CancellationToken token)
    {
        Check(destination);
        var temporary = destination + ".krosforge-server.tmp";
        Check(temporary);
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token);
            token.ThrowIfCancellationRequested();
            Check(destination); Check(temporary);
            File.Move(temporary, destination, true);
        }
        finally { Check(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static byte[] Rename(byte[] original, string name)
    {
        name = ServerName.Normalize(name);
        try
        {
            using var input = new MemoryStream(original, false);
            using var reader = new BinaryReader(input, new UTF8Encoding(false, true));
            reader.ReadString(); // Preserve the native language header verbatim.
            var count = reader.ReadInt32();
            if (count < 0 || count > (input.Length - input.Position) / 8) throw new IOException("ErrorServerPresentation");
            var field = -1;
            for (var i = 0; i < count; i++)
            {
                var key = reader.ReadInt32();
                var position = checked((int)input.Position);
                var offset = reader.ReadInt32();
                if (key != NameKey) continue;
                if (field >= 0 || offset < input.Position + (count - i - 1) * 8L || offset >= input.Length)
                    throw new IOException("ErrorServerPresentation");
                field = position;
                var next = input.Position;
                input.Position = offset;
                reader.ReadString();
                input.Position = next;
            }
            if (field < 0) throw new IOException("ErrorServerPresentation");
            using var output = new MemoryStream();
            using var writer = new BinaryWriter(output, new UTF8Encoding(false, true), true);
            writer.Write(original);
            writer.Write(name);
            output.Position = field;
            writer.Write(original.Length);
            return output.ToArray();
        }
        catch (Exception e) when (e is EndOfStreamException or FormatException or DecoderFallbackException or OverflowException)
        { throw new IOException("ErrorServerPresentation", e); }
    }
}
