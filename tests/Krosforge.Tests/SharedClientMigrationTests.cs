using System.Text.Json;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed class SharedClientMigrationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "shared-migration-" + Guid.NewGuid().ToString("N"));
    private string Clients => Path.Combine(root, "clients");
    private string AddLegacy()
    {
        var directory = Path.Combine(Clients, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "payload"), "unchanged assets");
        File.WriteAllText(Path.Combine(directory, "krosforge-client.json"), JsonSerializer.Serialize(new PreparedClient(0, "synthetic", "Krscli", "P0001")));
        return directory;
    }
    private static PreparedClient Validate(string directory)
    {
        SafePaths.CheckTree(directory);
        var marker = JsonSerializer.Deserialize<PreparedClient>(File.ReadAllText(Path.Combine(directory, "krosforge-client.json")))!;
        Assert.Equal("unchanged assets", File.ReadAllText(Path.Combine(directory, "payload")));
        return marker;
    }
    [Fact]
    public async Task ReusesExistingCopyAndRemovesOnlyValidatedDuplicates()
    {
        var first = AddLegacy();
        var second = AddLegacy();
        var unknown = Path.Combine(Clients, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "keep"), "not a managed copy");
        var modified = File.GetLastWriteTimeUtc(Path.Combine(first, "payload"));
        File.SetLastWriteTimeUtc(Path.Combine(second, "payload"), modified);
        await SharedClientMigration.MigrateAsync(root, Validate, TestContext.Current.CancellationToken);
        var shared = ClientPreparation.Target(root);
        Assert.Equal(2, Validate(shared).Layout);
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(shared, "payload")));
        Assert.False(Directory.Exists(first));
        Assert.False(Directory.Exists(second));
        Assert.Equal("not a managed copy", File.ReadAllText(Path.Combine(unknown, "keep")));
        Assert.Equal(2, Directory.GetDirectories(Clients).Length);
    }
    [Fact]
    public async Task RestartFinishesRetiredCopyCleanupAndReusesSharedFiles()
    {
        AddLegacy();
        await SharedClientMigration.MigrateAsync(root, Validate, TestContext.Current.CancellationToken);
        var retired = Path.Combine(Clients, ".retired-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(retired);
        File.WriteAllText(retired + ".json", JsonSerializer.Serialize(Guid.ParseExact(Path.GetFileName(retired)[9..], "N")));
        File.WriteAllText(Path.Combine(retired, "remaining"), "interrupted cleanup");
        await SharedClientMigration.MigrateAsync(root, Validate, TestContext.Current.CancellationToken);
        Assert.Equal(ClientPreparation.Target(root), Assert.Single(Directory.GetDirectories(Clients)));
        Assert.Equal(2, Validate(ClientPreparation.Target(root)).Layout);
    }
    [Fact]
    public async Task RetirementWithoutOwnershipJournalIsPreserved()
    {
        AddLegacy();
        var unknown = Path.Combine(Clients, ".retired-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(unknown);
        File.WriteAllText(Path.Combine(unknown, "keep"), "unrecognized directory");
        await SharedClientMigration.MigrateAsync(root, Validate, TestContext.Current.CancellationToken);
        Assert.Equal("unrecognized directory", File.ReadAllText(Path.Combine(unknown, "keep")));
    }
    [Fact]
    public async Task CancellationLeavesLegacyCopyAvailableForRetry()
    {
        var legacy = AddLegacy();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SharedClientMigration.MigrateAsync(root, Validate, cancel.Token));
        Assert.True(Directory.Exists(legacy));
        Assert.False(Directory.Exists(ClientPreparation.Target(root)));
        await SharedClientMigration.MigrateAsync(root, Validate, TestContext.Current.CancellationToken);
        Assert.Equal(2, Validate(ClientPreparation.Target(root)).Layout);
    }
    [Fact]
    public async Task InvalidSharedCopyDoesNotTriggerLegacyDeletion()
    {
        var legacy = AddLegacy();
        Directory.CreateDirectory(ClientPreparation.Target(root));
        await Assert.ThrowsAsync<FileNotFoundException>(() => SharedClientMigration.MigrateAsync(root, Validate, TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(legacy));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
