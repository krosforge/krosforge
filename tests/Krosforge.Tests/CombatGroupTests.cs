using Krosforge.Core;
using Krosforge.Runtime.Native;
using Krosforge.Runtime.Native.Protocol.Protobuf;
using Xunit;
namespace Krosforge.Tests;

public sealed partial class WorldSessionTests
{
    private static WorldEntryCatalog GroupWorld() => new(new(100, 0, 3, 1), new(14, 560, 50), [],
        new EntryMap(100, 25, Enumerable.Range(0, 560).ToHashSet())
        {
            Battles = [new(200, 26, new(14, Enumerable.Range(0, 560).Select(id =>
                new CombatCell(id, true, true, 0, 1, id == 100 ? 0 : id is 128 or 129 ? 1 : null))))]
        });
    private static CombatCatalog GroupEncounter(WorldEntryCatalog world)
    {
        var battle = world.Map(100).Battles[0];
        return new(world, new CombatEncounter(100, -30001, 1, 3, 2, 4, 100, 128,
            new(7, 1, 1, 15, 2, 3, new(9, [], [], [100])) { Experience = 50, Spells = [2] }, battle)
        {
            Reward = new(1, true),
            Companions = [new(-2, 129, new(8, 2, 1, 25, 4, 2, new(10, [], [], [110])) { Experience = 80, Spells = [2] })]
        });
    }
    [Fact]
    public void MultiMonsterVictoryWaitsForEveryOpponentAndCommitsTheirRewardsOnce()
    {
        var loot = new CombatLootCatalog(new()
        {
            [7] = [new(100, [100, 100], 63, DropCondition.Parse(""), false, [])],
            [8] = [new(100, [100, 100], 63, DropCondition.Parse(""), false, [])]
        }, new() { [100] = new(100, []) });
        var rewards = new CombatRewardCalculator(new([1], 5, 10), new(2.5, 2_000_000_000),
            new("Local", 2.5m), loot, new(85, 150, 117, 1.5, 0.26, 2.5));
        using var f = new MovementFixture(deferTimers: true, catalog: GroupWorld(), combat: GroupEncounter,
            spells: ResultSpells(), outcomes: true, rewards: rewards);
        var (a, notices) = f.Join("Alpha"); Fight(f, a);
        var turns = a.Fight!.Turns!;
        Assert.Equal(3, turns.Fighters.Count);
        Assert.Equal(15, turns.Fighters.Single(fighter => fighter.Id == -1).Life);
        Assert.Equal(25, turns.Fighters.Single(fighter => fighter.Id == -2).Life);
        Assert.Equal(2, turns.Fighters.Single(fighter => fighter.Id == -2).MaximumMp);
        f.Service.Cast(a, 1, 128);
        Assert.False(turns.Finished); Assert.Null(a.Fight.Receipt); Assert.Equal(0, a.Rewards.Experience);
        f.Service.CombatAcknowledge(a, a.Fight.Sequence!.Value);
        f.Service.Cast(a, 1, 129); f.Service.CombatAcknowledge(a, a.Fight.Sequence!.Value);
        f.Service.Cast(a, 1, 129);
        Assert.True(turns.Finished); Assert.Equal(325, a.Rewards.Experience);
        Assert.Equal(2, Assert.Single(a.Rewards.Items).Quantity);
        var sequence = a.Fight.Sequence!.Value;
        f.Service.CombatAcknowledge(a, sequence); f.Service.CombatAcknowledge(a, sequence);
        var result = Assert.Single(notices.OfType<FightCompleted>());
        Assert.Equal(3, result.Fighters.Count); Assert.Equal(2, result.Summary.EnemiesDefeated);
        Assert.Equal(325, result.Receipt.Award.Experience); Assert.Equal(1, result.Receipt.Balance);
        a.Dispose(); using var restored = f.Service.Select(a.Profile, a.Account, new object(), a.Character.Id)!;
        Assert.Equal(325, restored.Rewards.Experience); Assert.Equal(2, Assert.Single(restored.Rewards.Items).Quantity);
    }
    [Fact]
    public void GroupAndFighterMessagesRetainEachMonstersIdentityAppearanceAndGrade()
    {
        var world = GroupWorld(); var encounter = GroupEncounter(world).Encounters.Single();
        static ProtoObject Read(byte[] bytes) => ProtoObject.Read(bytes, 4096);
        var actor = Read(CombatWire.Group(encounter));
        var group = Read(Read(Read(actor.Bytes(3)).Bytes(1)).Bytes(5)); group = Read(group.Bytes(6));
        var leader = Read(group.Bytes(4)); var companion = Read(group.Repeated(3).Single().Bytes);
        Assert.Equal(7UL, leader.UInt64(5)); Assert.Equal(1UL, leader.UInt64(2));
        Assert.Equal(8UL, companion.UInt64(5)); Assert.Equal(2UL, companion.UInt64(2));
        Assert.Equal(10UL, Read(companion.Bytes(3)).UInt64(1));
        CharacterWire character = new(1, "Hero", 1, 0, 1, 1, 1, new(1, [], [], [100]));
        CombatView view = new(1, encounter, character, world.Attributes,
            [new(1, 0, 100, true), new(-1, 1, 128, false, true), new(-2, 1, 129, false, true)]);
        var fighter = Read(CombatWire.Fighter(view, view.Positions[2]));
        Assert.Equal(unchecked((ulong)-2L), fighter.UInt64(1)); Assert.Equal(129UL, Read(fighter.Bytes(2)).UInt64(3));
        var detail = Read(fighter.Bytes(3)); Assert.Equal(10UL, Read(detail.Bytes(2)).UInt64(1));
        var identity = Read(Read(Read(detail.Bytes(4)).Bytes(6)).Bytes(3));
        Assert.Equal(8UL, identity.UInt64(3)); Assert.Equal(2UL, identity.UInt64(1)); Assert.Equal(1UL, identity.UInt64(2));
    }
    [Fact]
    public void GroupCatalogRejectsOverlappingCellsDuplicateActorsAndTooManyOpponents()
    {
        var world = GroupWorld(); var encounter = GroupEncounter(world).Encounters.Single();
        Assert.Throws<ArgumentException>(() => new CombatCatalog(world, encounter with
            { Companions = [encounter.Companions[0] with { Cell = encounter.MonsterCell }] }));
        Assert.Throws<InvalidDataException>(() => new CombatCatalog(world, encounter with
            { Companions = [encounter.Companions[0] with { Actor = -1 }] }));
        Assert.Throws<InvalidDataException>(() => new CombatCatalog(world, encounter with
            { Companions = System.Collections.Immutable.ImmutableArray.CreateRange(Enumerable.Repeat(encounter.Companions[0], 8)) }));
    }
}
