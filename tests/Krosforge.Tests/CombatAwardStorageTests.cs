using Krosforge.Core;
using Krosforge.Runtime;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Krosforge.Tests;

public sealed class CombatAwardStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "combat-awards-" + Guid.NewGuid().ToString("N"));
    private static readonly WorldLocation Initial = new(100, 10, 3, 1);
    private (Guid Profile, long Character, CombatOutcomeStore Store) Create()
    {
        using var profiles = new ProfileStore(root); var profile = profiles.Create("Alpha");
        new CharacterStore(root).Create(profile.Id, 290, "Hero", new(1, ClientInstallation.SupportedBuild, 1, 0, 1, 1, [1,2,3,4,5,6]), out var character);
        new LocationStore(root).LoadOrCreate(profile.Id, 290, character, Initial, _ => { });
        return (profile.Id, character, new(root));
    }
    [Fact]
    public async Task ConcurrentCompletionsCreditExperienceAndLootOnceAndKeepStableItemIds()
    {
        var (profile, character, store) = Create(); var id = store.Begin(profile, 290, character, -2, Initial);
        CombatAward award = new(125, [new(100, 2), new(100, 3), new(101, 1)]);
        var receipts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            store.Complete(profile, 290, character, id, "victory", 3, 1000, award: award))));
        foreach (var receipt in receipts)
        {
            Assert.Equal(3, receipt.Balance); Assert.Equal(125, receipt.State.Experience);
            Assert.Equal(2, receipt.State.Items.Length); Assert.Equal(5, receipt.State.Items[0].Quantity);
        }
        var first = receipts[0].State.Items[0].Id;
        var next = store.Begin(profile, 290, character, -3, Initial);
        store.Complete(profile, 290, character, next, "victory", 2, 2000, award: new(25, [new(100, 1)]));
        var restored = new CombatOutcomeStore(root).Read(profile, 290, character);
        Assert.Equal(150, restored.Experience); Assert.Equal(first, restored.Items[0].Id); Assert.Equal(6, restored.Items[0].Quantity);
        Assert.Throws<InvalidDataException>(() => store.Complete(profile, 290, character, id, "victory", 3, 1000, award: new(126, award.Items)));
        var cleanup = store.Complete(profile, 290, character, id, "interrupted", 0, 0);
        Assert.Equal(125, cleanup.Award.Experience); Assert.Equal(150, cleanup.State.Experience);
        Assert.Equal(5, cleanup.Balance);
    }
    [Fact]
    public void FailureRollsBackEveryRewardAndRetryCommitsThemTogether()
    {
        var (profile, character, store) = Create(); var id = store.Begin(profile, 290, character, -2, Initial);
        CombatAward award = new(250, [new(100, 3)]);
        Assert.Throws<IOException>(() => store.Complete(profile, 290, character, id, "victory", 7, 100,
            beforeCommit: () => throw new IOException("Injected failure"), award: award));
        var rolledBack = store.Read(profile, 290, character);
        Assert.Equal(0, rolledBack.Experience); Assert.Empty(rolledBack.Items);
        var receipt = store.Complete(profile, 290, character, id, "victory", 7, 100, award: award);
        Assert.Equal(7, receipt.Balance); Assert.Equal(250, receipt.State.Experience); Assert.Single(receipt.State.Items);
    }
    [Fact]
    public void InventoryOverflowRollsBackExperienceCurrencyAndOutcome()
    {
        var (profile, character, store) = Create(); var id = store.Begin(profile, 290, character, -2, Initial);
        store.Complete(profile, 290, character, id, "victory", 2, 100, award: new(20, [new(100, int.MaxValue)]));
        var next = store.Begin(profile, 290, character, -2, Initial);
        Assert.Throws<SqliteException>(() => store.Complete(profile, 290, character, next, "victory", 7, 100, award: new(250, [new(100, 1)])));
        Assert.Equal(20, store.Read(profile, 290, character).Experience);
        var retry = store.Complete(profile, 290, character, next, "victory", 1, 100, award: new(5, [new(101, 1)]));
        Assert.Equal(3, retry.Balance); Assert.Equal(25, retry.State.Experience);
    }
    [Fact]
    public void InterruptedCombatDoesNotGrantAwardsAndReadRequiresOwnership()
    {
        var (profile, character, store) = Create(); var id = store.Begin(profile, 290, character, -2, Initial);
        Assert.Throws<ArgumentException>(() => store.Complete(profile, 290, character, id, "defeat", 0, 100, award: new(5, [])));
        Assert.Throws<ArgumentException>(() => store.Complete(profile, 290, character, id, "abandoned", 0, 100, award: new(0, [new(100, 1)])));
        store.Recover(profile, 290, character);
        Assert.Equal(0, store.Read(profile, 290, character).Experience);
        Assert.Throws<InvalidDataException>(() => store.Read(Guid.NewGuid(), 290, character));
        Assert.Throws<InvalidDataException>(() => store.Read(profile, 291, character));
    }
    [Fact]
    public void SpellBarPersistsByCharacterAndDistinguishesAnEmptyLayoutFromTheDefault()
    {
        var (profile, character, store) = Create();
        Assert.Null(store.Read(profile, 290, character).SpellBar);
        var layout = new SpellShortcut[] { new(0, 0), new(5, 13338) };
        store.SaveSpellBar(profile, 290, character, layout);
        Assert.Throws<InvalidDataException>(() => store.SaveSpellBar(Guid.NewGuid(), 290, character, []));
        Assert.Equal(layout, new CombatOutcomeStore(root).Read(profile, 290, character).SpellBar);
        store.SaveSpellBar(profile, 290, character, []);
        Assert.Empty(new CombatOutcomeStore(root).Read(profile, 290, character).SpellBar!);
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
