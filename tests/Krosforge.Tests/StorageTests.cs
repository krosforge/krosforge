using Krosforge.Runtime;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Krosforge.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "krosforge-test-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void ProfilesSurviveRestartAndRenamePreservesIdentity()
    {
        Guid id;
        using (var store = new ProfileStore(root))
        {
            id = store.Create("Principal").Id;
            store.Rename(id, "Novo nome");
        }
        using var reopened = new ProfileStore(root);
        Assert.Equal(id, Assert.Single(reopened.List()).Id);
        Assert.Equal("Novo nome", reopened.List()[0].Name);
    }
    [Fact]
    public void DuplicateNamesAreRejectedAndSlotsAreNotReused()
    {
        using var store = new ProfileStore(root);
        var a = store.Create(" Principal ");
        Assert.Throws<ArgumentException>(() => store.Create("principal"));
        store.Delete(a.Id, "Principal");
        var b = store.Create("Principal");
        Assert.True(b.Slot > a.Slot);
        Assert.NotEqual(a.Id, b.Id);
    }
    [Fact]
    public void DeletionRequiresExactConfirmationAndPreservesOtherAccounts()
    {
        using var store = new ProfileStore(root);
        var a = store.Create("Um");
        var b = store.Create("Dois");
        Assert.Throws<ArgumentException>(() => store.Delete(a.Id, "um"));
        Assert.Equal(2, store.List().Length);
        store.Delete(a.Id, "Um");
        Assert.Equal(b.Id, Assert.Single(store.List()).Id);
        Assert.Equal(a.Id, Assert.Single(store.PendingCleanup()));
    }
    [Fact]
    public void DeletionCascadesOnlyOwnedCharacters()
    {
        using var store = new ProfileStore(root);
        var a = store.Create("Um");
        var b = store.Create("Dois");
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "krosforge.db"));
        connection.Open();
        foreach (var profile in new[] { a, b })
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO characters(profile_id,realm_id,name,name_key,appearance) VALUES($id,1,$id,$id,'{}')";
            command.Parameters.AddWithValue("$id", profile.Id.ToString());
            command.ExecuteNonQuery();
        }
        store.Delete(a.Id, "Um");
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT profile_id FROM characters";
        Assert.Equal(b.Id.ToString(), query.ExecuteScalar());
    }
    [Fact]
    public void NewerSchemaIsNotOverwritten()
    {
        using (var store = new ProfileStore(root))
            store.Create("Um");
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "krosforge.db")))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=999";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new ProfileStore(root));
    }
    [Fact]
    public void CleanupCannotFollowSymlinks()
    {
        var id = Guid.NewGuid();
        var owned = Path.Combine(root, "clients", id.ToString("N"));
        var other = Path.Combine(root, "original");
        Directory.CreateDirectory(owned);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "keep"), "unchanged");
        if (OperatingSystem.IsWindows())
            return;
        Directory.CreateSymbolicLink(Path.Combine(owned, "link"), other);
        Assert.Throws<IOException>(() => SafePaths.DeleteOwnedProfile(root, id));
        Assert.True(File.Exists(Path.Combine(other, "keep")));
    }
    [Fact]
    public void DeletingProfilesNeverRemovesTheSharedClient()
    {
        using (var store = new ProfileStore(root))
        {
            var first = store.Create("First");
            var second = store.Create("Second");
            Directory.CreateDirectory(ClientPreparation.Target(root));
            File.WriteAllText(Path.Combine(ClientPreparation.Target(root), "keep.bin"), "shared assets");
            store.Delete(first.Id, first.Name);
            store.Delete(second.Id, second.Name);
        }
        using var service = new RuntimeService(root);
        Assert.Equal("shared assets", File.ReadAllText(Path.Combine(ClientPreparation.Target(root), "keep.bin")));
    }
    [Fact]
    public void CleanupResumesAfterRuntimeRestart()
    {
        Guid id;
        using (var store = new ProfileStore(root))
        {
            var p = store.Create("Conta");
            id = p.Id;
            Directory.CreateDirectory(Path.Combine(root, "clients", id.ToString("N")));
            store.Delete(id, "Conta");
        }
        using (var service = new RuntimeService(root))
        {
        }
        using var reopened = new ProfileStore(root);
        Assert.Empty(reopened.PendingCleanup());
        Assert.False(Directory.Exists(Path.Combine(root, "clients", id.ToString("N"))));
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }
}
