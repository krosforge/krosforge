using System.Security.Cryptography;
namespace Krosforge.Runtime.Native;

// Compatibility changes for the hash-pinned Windows client, build 3.7.14.18.
// See docs/native-client.md for the native consumers and recovery contract.
internal static class CharacterNavigation
{
    internal const string PatchedHash = "e51305f154af33fe0fb71584f105847b9e90ff88f074232eace4323d3f2ae7d4";
    internal sealed record Patch(int Offset, string Expected, string Replacement);
    internal static readonly Patch[] Patches =
    [
        // Selection rejection: clear only the pending selection, retaining the roster.
        new(25442635, "33d2488bcf488b6c2430488b5c2438488b7424404883c4205fe957e2ffff",
            "33c048898790000000488b6c2430488b5c2438488b7424404883c4205fc3"),
        // A forced return displays the roster; normal creation keeps automatic entry.
        new(32519623, "752d", "7536"),
        // Server handoff opens the roster. Creation's separate selection path stays intact.
        new(25421203, "e8f8530000", "33c0909090"),
        // Back from creation: carry the roster into the display event, including
        // when character selection has never been opened in this native session.
        new(24567216, "488b83700500004885c00f844c010000", "488bf0488b83700500004885c0741190"),
        new(24567307, "33d2488bc8488be8", "488bd64891488be9")
    ];

    internal static Task ApplyAsync(string source, string target, CancellationToken token)
    {
        ClientPreparation.EnsureDisjoint(source, target);
        return ApplyValidatedAsync(Path.Combine(target, "GameAssembly.dll"),
            ClientCompatibility.Hashes["GameAssembly.dll"], PatchedHash, Patches, token);
    }

    internal static async Task ApplyValidatedAsync(string path, string originalHash, string patchedHash,
        IReadOnlyList<Patch> patches, CancellationToken token, Action? beforePromotion = null)
    {
        token.ThrowIfCancellationRequested();
        Check(path);
        var bytes = await File.ReadAllBytesAsync(path, token);
        var currentHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (currentHash == patchedHash) return;
        if (currentHash != originalHash) throw new IOException("ErrorCopyInvalid");
        foreach (var patch in patches)
        {
            var expected = Convert.FromHexString(patch.Expected);
            var replacement = Convert.FromHexString(patch.Replacement);
            if (expected.Length == 0 || expected.Length != replacement.Length || patch.Offset < 0 ||
                patch.Offset > bytes.Length - expected.Length ||
                !bytes.AsSpan(patch.Offset, expected.Length).SequenceEqual(expected))
                throw new IOException("ErrorCopyInvalid");
            replacement.CopyTo(bytes, patch.Offset);
        }
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != patchedHash) throw new IOException("ErrorCopyInvalid");
        var temporary = path + ".navigation-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                await output.WriteAsync(bytes, token);
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
            beforePromotion?.Invoke();
            token.ThrowIfCancellationRequested();
            Check(path); Check(temporary);
            // Never edit a mapped executable in place. Failed promotion leaves the old file usable.
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) { Check(temporary); File.Delete(temporary); }
        }
    }

    private static void Check(string path)
    {
        SafePaths.CheckTree(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("ErrorCopyInvalid");
    }
}
