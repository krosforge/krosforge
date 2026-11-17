using System.Text.Json;
namespace Krosforge.Runtime.Native;

internal static class SharedClientMigration
{
    // Called under the launch manager's preparation lock, before any native session starts.
    internal static async Task MigrateAsync(string dataRoot, Func<string, PreparedClient> validate, CancellationToken token)
    {
        var shared = ClientPreparation.Target(dataRoot);
        var clients = Path.GetDirectoryName(shared)!;
        SafePaths.CheckTree(clients);
        if (!Directory.Exists(clients)) return;
        var legacy = Directory.GetDirectories(clients).Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _)).Order().ToArray();
        foreach (var path in legacy)
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(shared)) break;
            if (!TryValidate(path, validate, out var marker)) continue;
            var temporary = Path.Combine(path, ".shared-marker-" + Guid.NewGuid().ToString("N"));
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(marker! with { Layout = 2 }), token);
                File.Move(temporary, Path.Combine(path, "krosforge-client.json"), true);
                token.ThrowIfCancellationRequested();
                Directory.Move(path, shared);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (!Directory.Exists(shared)) return;
        validate(shared);
        // A distinct name makes interrupted cleanup recoverable without revalidating partially deleted files.
        foreach (var journal in Directory.GetFiles(clients, ".retired-*.json"))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(journal) & FileAttributes.ReparsePoint) != 0) continue;
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(journal)[9..], "N", out var id)) continue;
            try
            {
                if (JsonSerializer.Deserialize<Guid>(File.ReadAllText(journal)) != id) continue;
            }
            catch (JsonException) { continue; }
            SafePaths.DeleteOwnedDirectory(Path.Combine(clients, ".retired-" + id.ToString("N")));
            File.Delete(journal);
        }
        foreach (var path in legacy)
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(path) || !TryValidate(path, validate, out _)) continue;
            var retired = Path.Combine(clients, ".retired-" + Path.GetFileName(path));
            if (Directory.Exists(retired) || File.Exists(retired + ".json")) continue;
            var pending = retired + "." + Guid.NewGuid().ToString("N") + ".journal";
            try
            {
                await using (var journal = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(journal, Guid.ParseExact(Path.GetFileName(path), "N"), cancellationToken: token);
                    journal.Flush(flushToDisk: true);
                }
                File.Move(pending, retired + ".json");
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
            Directory.Move(path, retired);
            SafePaths.DeleteOwnedDirectory(retired);
            File.Delete(retired + ".json");
        }
    }
    private static bool TryValidate(string path, Func<string, PreparedClient> validate, out PreparedClient? marker)
    {
        try { marker = validate(path); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { marker = null; return false; }
    }
}
