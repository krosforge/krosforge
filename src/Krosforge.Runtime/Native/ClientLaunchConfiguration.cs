using System.Text;
namespace Krosforge.Runtime.Native;

internal static class ClientLaunchConfiguration
{
    internal static async Task WriteAsync(string target, string baseUrl, CancellationToken token)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Host != "127.0.0.1" || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new IOException("ErrorCopyInvalid");
        var versionFile = Path.Combine(target, "Dofus_Data", "StreamingAssets", "version");
        var parent = Path.GetDirectoryName(versionFile)!;
        SafePaths.CheckTree(parent);
        if ((File.GetAttributes(versionFile) & FileAttributes.ReparsePoint) != 0) throw new IOException("ErrorCopyInvalid");
        var lines = await File.ReadAllLinesAsync(versionFile, token);
        if (lines.Count(line => line.StartsWith("ConfigUrl=", StringComparison.Ordinal)) != 1) throw new IOException("ErrorCopyInvalid");
        var temporary = Path.Combine(parent, ".version-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            await using (var writer = new StreamWriter(output, new UTF8Encoding(false)))
            {
                foreach (var line in lines)
                    await writer.WriteLineAsync((line.StartsWith("ConfigUrl=", StringComparison.Ordinal) ? "ConfigUrl=" + baseUrl.TrimEnd('/') + "/config.json" : line).AsMemory(), token);
            }
            token.ThrowIfCancellationRequested();
            SafePaths.CheckTree(parent);
            if ((File.GetAttributes(versionFile) & FileAttributes.ReparsePoint) != 0) throw new IOException("ErrorCopyInvalid");
            File.Move(temporary, versionFile, true);
        }
        finally
        {
            SafePaths.CheckTree(parent);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
