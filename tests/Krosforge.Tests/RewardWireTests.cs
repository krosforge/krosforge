using System.Text.Json;
using Krosforge.Core;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Krosforge.Runtime.Native.Protocol.Protobuf;
using Xunit;
namespace Krosforge.Tests;

public sealed class RewardWireTests
{
    private static ProtoObject Read(byte[] bytes) => ProtoObject.Read(bytes, 4096);
    [Fact]
    public void SpellBarAssignmentAndSnapshotUseTheNativeSpellVariant()
    {
        var request = Read(Convert.FromHexString("080112071203089A681801"));
        Assert.True(SpellShortcutWire.IsSpellBar(request));
        var shortcut = SpellShortcutWire.ReadAssignment(request);
        Assert.Equal(new SpellShortcut(1, 13338), shortcut);
        var snapshot = Read(SpellShortcutWire.Snapshot([shortcut]));
        Assert.Equal(1UL, snapshot.UInt64(2));
        var entry = Read(Assert.Single(snapshot.Repeated(1)).Bytes);
        Assert.Equal(1UL, entry.UInt64(3));
        Assert.Equal(13338UL, Read(entry.Bytes(2)).UInt64(1));
    }
    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void RoleplayLevelNotificationPrecedesTheAuthoritativeStatistics(int current)
    {
        var level = new CharacterLeveled(current, new(100, 6, 3, 650) { AvailablePoints = (current - 1) * 5 });
        var events = WorldEntryWire.Events(level).Select(frame => Read(Read(Read(frame).Bytes(3)).Bytes(1))).ToArray();
        Assert.Equal(new[] { "type.ankama.com/kqp", "type.ankama.com/krg" }, events.Select(e => e.Text(1)));
        Assert.Equal((ulong)current, Read(events[0].Bytes(2)).UInt64(1));
        Assert.Equal((ulong)((current - 1) * 5), Stat(Read(events[1].Bytes(2)), 3));
    }
    internal static ulong Stat(ProtoObject payload, int id, bool usable = true)
    {
        var entry = Read(payload.Bytes(1)).Repeated(1).Select(field => Read(field.Bytes)).Single(stat => stat.UInt64(2) == (ulong)id);
        return usable ? Read(entry.Bytes(1)).UInt64(4) : Read(entry.Bytes(3)).UInt64(2);
    }
    [Fact]
    public void MapSnapshotPublishesConfiguredExperienceAndDropPercentagesWithoutRounding()
    {
        var calculator = new CombatRewardCalculator(new GroupExperienceRules([1], 5, 10), new(2.5, 2_000_000_000), new("Local", 2.55m, 0m));
        var snapshot = new MapSnapshot(new(100, 25, new HashSet<int> { 0 }), []) { Rates = calculator.Presentation };
        var rates = WorldEntryWire.Events(snapshot).Select(frame => Read(Read(Read(frame).Bytes(3)).Bytes(1)))
            .Single(any => any.Text(1) == "type.ankama.com/hme");
        var body = Read(rates.Bytes(2)); Assert.Equal(255UL, body.UInt64(3)); Assert.Equal(0UL, body.UInt64(4)); Assert.Equal(100UL, body.UInt64(5));
        calculator.Configure(new("Local", 0m, 100m));
        Assert.Equal(new RewardRates(0, 10000), calculator.Presentation);
    }
    [Fact]
    public void InstalledItemEffectsResolveManagedReferencesBeforeInventoryProjection()
    {
        var root = Environment.GetEnvironmentVariable("KROSFORGE_TEST_CLIENT");
        Assert.SkipWhen(root is null, "Requires the separately installed reference client.");
        var rows = CreationDataReader.Read(Path.Combine(root!, "Dofus_Data/StreamingAssets/Content/Data/data_assets_itemsdataroot.asset.bundle"),
            "ea63bf34ee28072092c5063a6b81870b87dbc37b61723a08e1c605e0f47d6ccd", maxOutput: 33554432,
            selectedKeys: new HashSet<long> { 16504, 34618 }, resolveReferences: true);
        var item = rows.Single(row => (int)row["id"] == 16504);
        var effects = ((object[])((Dictionary<string, object>)item["possibleEffects"]).Single().Value).Cast<Dictionary<string, object>>().ToArray();
        Assert.Equal(new[] { 125, 138 }, effects.Select(effect => Convert.ToInt32(effect["actionId"])));
        Assert.Equal(11, Convert.ToInt32(effects[0]["diceNum"])); Assert.Equal(15, Convert.ToInt32(effects[0]["diceSide"]));
        Assert.DoesNotContain(effects, effect => effect.ContainsKey("rid"));
    }
    [Fact]
    public void InventoryPublishesPersistentIdsQuantitiesBagPositionAndTypedEffects()
    {
        var effects = JsonSerializer.Serialize<InventoryEffect[]>([new(110, 15, 0, 0), new(111, 2, 5, 1)]);
        var snapshot = Read(InventoryWire.Snapshot(7, [new(42, 100, 3, effects), new(43, 101, 1, "[]")]));
        Assert.Equal(7UL, snapshot.UInt64(4)); Assert.True(snapshot.Has(3));
        var item = Read(snapshot.Repeated(2).First().Bytes); Assert.Equal(63UL, item.UInt64(4));
        var common = Read(item.Bytes(1)); Assert.Equal(42UL, common.UInt64(5));
        Assert.Equal(100UL, common.UInt64(4)); Assert.Equal(3UL, common.UInt64(3));
        var first = Read(common.Repeated(7).First().Bytes); Assert.Equal(110UL, first.UInt64(10)); Assert.Equal(15UL, first.UInt64(5));
        var second = Read(common.Repeated(7).Last().Bytes); Assert.Equal(111UL, second.UInt64(10));
        var dice = Read(second.Bytes(9)); Assert.Equal(2UL, dice.UInt64(1)); Assert.Equal(5UL, dice.UInt64(2)); Assert.Equal(1UL, dice.UInt64(4));
    }
    [Fact]
    public void CombatResultSeparatesExperienceAndItemDeltasFromCumulativeState()
    {
        CharacterWire character = new(1, "Hero", 1, 0, 1, 1, 2, new(1, [], [], [100]));
        CombatReceipt receipt = new(Guid.NewGuid(), "victory", 1, 7, 1000)
        {
            Award = new(125, [new(100, 2), new(100, 3)]),
            State = new(235, [new(42, 100, 8, "[]")])
        };
        EntryAttributes attributes = new(100, 6, 3, 650) { Experience = 235, ExperienceFloor = 110, Currency = 7 };
        var result = new FightCompleted(character, attributes, [new(1, true, true, 2), new(-1, false, false, null)], receipt, new());
        var frames = CombatWire.Events(result).Select(frame =>
        {
            var any = Read(Read(Read(frame).Bytes(3)).Bytes(1));
            return (Type: any.Text(1)[WorldProtocol.Prefix.Length..], Payload: any.Bytes(2));
        }).ToArray();
        var outcome = Read(frames.Single(frame => frame.Type == "jwp").Payload);
        var player = outcome.Repeated(4).Select(field => Read(field.Bytes)).Single(row => Read(row.Bytes(2)).UInt64(5) == 1);
        var identity = Read(Read(player.Bytes(2)).Bytes(3)); Assert.Equal(2UL, identity.UInt64(2));
        var xp = Read(Read(identity.Repeated(1).Single().Bytes).Bytes(1));
        Assert.Equal(235UL, xp.UInt64(3)); Assert.Equal(110UL, xp.UInt64(4)); Assert.Equal(650UL, xp.UInt64(6));
        Assert.Equal(125UL, xp.UInt64(8)); Assert.Equal(1UL, xp.UInt64(5)); Assert.Equal(1UL, xp.UInt64(9));
        var prize = Read(player.Bytes(3)); Assert.Equal(1UL, prize.UInt64(2));
        var item = Read(Read(prize.Bytes(1)).Repeated(3).Single().Bytes);
        Assert.Equal(5UL, item.UInt64(1)); Assert.Equal(100UL, item.UInt64(3));
        var inventory = Read(frames.Single(frame => frame.Type == "irl").Payload);
        Assert.Equal(7UL, inventory.UInt64(4));
        Assert.Equal(7UL, Read(frames.Single(frame => frame.Type == "ith").Payload).UInt64(2));
        var common = Read(Read(inventory.Repeated(2).Single().Bytes).Bytes(1)); Assert.Equal(8UL, common.UInt64(3));
        Assert.Equal(235UL, Read(Read(frames.Single(frame => frame.Type == "krg").Payload).Bytes(1)).UInt64(11));
        Assert.Equal(7UL, Read(Read(frames.Single(frame => frame.Type == "krg").Payload).Bytes(1)).UInt64(6));
    }
}
