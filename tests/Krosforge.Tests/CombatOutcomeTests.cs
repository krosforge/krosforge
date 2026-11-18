using Krosforge.Core;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Krosforge.Runtime.Native.Protocol.Protobuf;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Krosforge.Tests;

public sealed class CombatOutcomeStorageTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "combat-outcome-" + Guid.NewGuid().ToString("N"));
    private static readonly WorldLocation Initial = new(100, 10, 3, 1);
    private (Guid Profile, long Character, CombatOutcomeStore Store) Create()
    {
        using var profiles = new ProfileStore(root); var profile = profiles.Create("Alpha");
        var characters = new CharacterStore(root);
        characters.Create(profile.Id, 290, "Hero", new(1, ClientInstallation.SupportedBuild, 1, 0, 1, 1, [1,2,3,4,5,6]), out var character);
        new LocationStore(root).LoadOrCreate(profile.Id, 290, character, Initial, _ => { });
        return (profile.Id, character, new(root));
    }
    [Fact]
    public async Task ConcurrentDuplicateVictoryCreditsOnlyOnceAndSurvivesReopen()
    {
        var (profile, character, store) = Create();
        var id = store.Begin(profile, 290, character, -30001, Initial);
        var receipts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.Complete(profile, 290, character, id, "victory", 7, 1000))));
        Assert.All(receipts, r => { Assert.Equal(7, r.Balance); Assert.Equal(7, r.Reward); Assert.Equal("victory", r.Status); });
        Assert.Equal(7, new CombatOutcomeStore(root).Recover(profile, 290, character));
        Assert.Throws<InvalidDataException>(() => store.Complete(profile, 290, character, id, "victory", 8, 1000));
        var cleanup = store.Complete(profile, 290, character, id, "interrupted", 0, 0);
        Assert.Equal("victory", cleanup.Status); Assert.Equal(7, cleanup.Balance);
        Assert.Equal(Initial, new LocationStore(root).LoadOrCreate(profile, 290, character, Initial, _ => { }));
    }
    [Fact]
    public void FailedPayoutRollsBackReceiptAndBalanceAndCanRetry()
    {
        var (profile, character, store) = Create(); var id = store.Begin(profile, 290, character, -30001, Initial);
        Assert.Throws<IOException>(() => store.Complete(profile, 290, character, id, "victory", 4, 500, () => throw new IOException("Injected failure")));
        using (var db = new SqliteConnection("Data Source=" + Path.Combine(root, "krosforge.db")))
        {
            db.Open(); using var c = db.CreateCommand();
            c.CommandText = "SELECT balance FROM character_wallets"; Assert.Equal(0L, c.ExecuteScalar());
            c.CommandText = "SELECT status FROM combat_outcomes"; Assert.Equal("active", c.ExecuteScalar());
        }
        Assert.Equal(4, store.Complete(profile, 290, character, id, "victory", 4, 500).Balance);
    }
    [Fact]
    public void RecoveryClosesUnfinishedFightWithoutRewardAndAllowsNewEncounter()
    {
        var (profile, character, store) = Create(); var abandoned = store.Begin(profile, 290, character, -30001, Initial);
        Assert.Throws<SqliteException>(() => store.Begin(profile, 290, character, -30001, Initial));
        Assert.Equal(0, new CombatOutcomeStore(root).Recover(profile, 290, character));
        Assert.Throws<InvalidDataException>(() => store.Complete(profile, 290, character, abandoned, "victory", 1, 1));
        var next = store.Begin(profile, 290, character, -30001, Initial); Assert.NotEqual(abandoned, next);
        Assert.Equal(0, store.Complete(profile, 290, character, next, "defeat", 0, 200).Balance);
    }
    [Fact]
    public void OwnershipLocationAndDeletedCharactersCannotAwardCurrency()
    {
        var (profile, character, store) = Create();
        Assert.Throws<InvalidDataException>(() => store.Begin(Guid.NewGuid(), 290, character, -30001, Initial));
        Assert.Throws<InvalidDataException>(() => store.Begin(profile, 291, character, -30001, Initial));
        Assert.Throws<InvalidDataException>(() => store.Begin(profile, 290, character, -30001, Initial with { Revision = 2 }));
        var id = store.Begin(profile, 290, character, -30001, Initial);
        Assert.Throws<InvalidDataException>(() => store.Complete(Guid.NewGuid(), 290, character, id, "victory", 1, 1));
        Assert.True(new CharacterStore(root).Delete(profile, 290, character));
        Assert.Throws<InvalidDataException>(() => store.Complete(profile, 290, character, id, "victory", 1, 1));
        using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "krosforge.db")); db.Open();
        using var c = db.CreateCommand(); c.CommandText = "SELECT (SELECT COUNT(*) FROM combat_outcomes)+(SELECT COUNT(*) FROM character_wallets)";
        Assert.Equal(0L, c.ExecuteScalar());
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}

public sealed partial class WorldSessionTests
{
    private static CombatCatalog ResultEncounter(WorldEntryCatalog world) => new(world,
        TurnEncounter(world).Encounters.Single() with { Reward = new(1, true) });
    private static CombatSpellCatalog ResultSpells(bool monsterWins = false)
    {
        CombatSpell spell = new(1, 1, 1, 2, 1, 8, true, true, 0, new(CombatElement.Fire, monsterWins ? 1 : 15, monsterWins ? 1 : 15), null);
        return new([spell], monsters: new Dictionary<int, System.Collections.Immutable.ImmutableArray<CombatSpell>>
            { [2] = [spell with { Id = 2, Damage = new(CombatElement.Fire, 100, 100) }] });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VictoryIsDurableBeforeFinalAnimationAckAndReconnectNeverAwardsAgain(bool disconnectBeforeAck)
    {
        using var f = new MovementFixture(deferTimers: true, catalog: TurnWorld(), combat: ResultEncounter, spells: ResultSpells(), outcomes: true);
        var (a, notices) = f.Join("Alpha"); Fight(f, a); var returned = a.Location;
        f.Service.Cast(a, 1, 128); var sequence = a.Fight!.Sequence!.Value;
        Assert.Equal(1, a.Currency); Assert.Equal("victory", a.Fight.Receipt!.Status);
        Assert.Empty(notices.OfType<FightCompleted>());
        if (!disconnectBeforeAck)
        {
            f.Service.CombatAcknowledge(a, sequence);
            var result = Assert.Single(notices.OfType<FightCompleted>());
            Assert.Equal(1, result.Receipt.Reward); Assert.Null(a.Fight);
            f.Service.CombatAcknowledge(a, sequence); Assert.Single(notices.OfType<FightCompleted>());
            f.Service.Join(a, 100); Assert.True(a.MapReady);
        }
        a.Dispose();
        var restarted = new WorldEntryService(new(f.Characters, SyntheticCatalog()), f.Locations, f.Catalog, f.Clock,
            ResultEncounter(f.Catalog), ResultSpells(), new(f.Root));
        using var restored = restarted.Select(a.Profile, a.Account, new object(), a.Character.Id)!;
        Assert.Equal(1, restored.Currency); Assert.Equal(returned, restored.Location);
        restored.ContextCreated = true; restarted.Join(restored, 100); restarted.Attack(restored, -30001);
        Assert.NotNull(restored.Fight); Assert.NotEqual(a.Fight?.DurableId, restored.Fight.DurableId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefeatAndForfeitPublishNoRewardAndRestoreExploration(bool forfeit)
    {
        using var f = new MovementFixture(deferTimers: true, catalog: TurnWorld(), combat: ResultEncounter, spells: ResultSpells(true), outcomes: true);
        var (a, notices) = f.Join("Alpha"); Fight(f, a);
        if (forfeit) f.Service.Forfeit(a);
        else
        {
            f.Service.EndCombatTurn(a); f.Service.CombatTurnReady(a, true);
            f.Clock.Advance(TimeSpan.FromMilliseconds(250)); f.Service.AdvanceCombat(a);
            Assert.Equal("defeat", a.Fight!.Receipt!.Status);
            f.Service.CombatAcknowledge(a, a.Fight.Sequence!.Value);
        }
        var result = Assert.Single(notices.OfType<FightCompleted>());
        Assert.Equal(0, result.Receipt.Reward); Assert.Equal(0, a.Currency); Assert.Null(a.Fight);
        Assert.Equal(forfeit ? "abandoned" : "defeat", result.Receipt.Status);
        Assert.Equal(forfeit ? 0 : 100, result.Summary.DamageTaken);
        Assert.Equal(0, result.Summary.DamageDealt); Assert.Equal(0, result.Summary.EnemiesDefeated);
        Assert.Equal(f.Catalog.Initial, a.Location); f.Service.Join(a, 100); Assert.True(a.MapReady);
    }
    [Fact]
    public void ResultEnvelopeUsesIndependentSignedIdentityOutcomeAndCurrencyFields()
    {
        using var f = new MovementFixture(deferTimers: true, catalog: TurnWorld(), combat: ResultEncounter, spells: ResultSpells(), outcomes: true);
        var (a, notices) = f.Join("Alpha"); Fight(f, a); f.Service.Cast(a, 1, 128);
        f.Service.CombatAcknowledge(a, a.Fight!.Sequence!.Value);
        var messages = CombatWire.Events(Assert.Single(notices.OfType<FightCompleted>()));
        ProtoObject Body(int index, string expected)
        {
            var any = ProtoObject.Read(ProtoObject.Read(ProtoObject.Read(messages[index], 4096).Bytes(3), 4096).Bytes(1), 4096);
            Assert.Equal("type.ankama.com/" + expected, any.Text(1)); return ProtoObject.Read(any.Bytes(2), 4096);
        }
        var result = Body(0, "jwp");
        Assert.Equal(2UL, ProtoObject.Read(Assert.Single(result.Repeated(2)).Bytes, 4096).UInt64(2));
        var rows = result.Repeated(4).Select(v => ProtoObject.Read(v.Bytes, 4096))
            .ToDictionary(v => ProtoObject.Read(v.Bytes(2), 4096).UInt64(5));
        Assert.Equal(2, rows.Count); Assert.Equal(2UL, rows[(ulong)a.Character.Id].UInt64(1));
        Assert.Equal(0UL, rows[ulong.MaxValue].UInt64(1, false));
        Assert.Equal(1UL, ProtoObject.Read(rows[(ulong)a.Character.Id].Bytes(3), 4096).UInt64(2));
        var summary = ProtoObject.Read(Body(1, "jwh").Bytes(2), 4096);
        Assert.Equal(15UL, summary.UInt64(4)); Assert.Equal(0UL, summary.UInt64(5, false)); Assert.Equal(1UL, summary.UInt64(7));
        Assert.Equal(1UL, Body(2, "irl").UInt64(4)); Assert.NotEmpty(Body(3, "krg").Bytes(1));
    }
}
