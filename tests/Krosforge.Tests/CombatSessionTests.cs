using System.Net.Sockets;
using Krosforge.Core;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Krosforge.Runtime.Native.Protocol.Protobuf;
using Xunit;
namespace Krosforge.Tests;

public sealed partial class WorldSessionTests
{
    private static WorldEntryCatalog CombatWorld() => new(new(100, 0, 3, 1), new(14, 560, 1000), [],
        new EntryMap(100, 25, Enumerable.Range(0, 6).ToHashSet()) { Battles = [new(200, 26, CombatPreparationTests.Board())] });
    private static CombatCatalog Encounters(WorldEntryCatalog world, int groupCell = 5) => new(world,
        new CombatEncounter(100, -30001, groupCell, 3, 2, 4, 2, 5, new(7, 1, 1, 15, 2, 3, new(9, [], [], [100])), world.Map(100).Battles[0]));
    [Fact]
    public void BattleRotationKeepsThePreviewUntilTheFightEndsAndVariesPlacementsPerCharacter()
    {
        BattleMap first = new(200, 26, CombatPreparationTests.Board());
        BattleMap second = new(201, 26, CombatPreparationTests.Board());
        BattleMap third = new(200, 26, new(14, Enumerable.Range(0, 6)
            .Select(id => new CombatCell(id, true, true, 0, 1, id < 3 ? 1 : 0))));
        var world = new WorldEntryCatalog(new(100, 0, 3, 1),
            new EntryMap(100, 25, Enumerable.Range(0, 6).ToHashSet()) { Battles = [first, second, third] });
        using var fixture = new MovementFixture(catalog: world, combat: catalog => Encounters(catalog, 1));
        var (alpha, notices) = fixture.Join("Alpha");
        var (beta, betaNotices) = fixture.Join("Beta");
        foreach (var expected in new[] { first, first, second, third, first }.Select((map, index) => (map, index)))
        {
            fixture.Service.PreviewBattleMap(alpha);
            fixture.Service.PreviewBattleMap(alpha);
            var previews = notices.OfType<BattleMapPreview>().TakeLast(2).ToArray();
            Assert.Equal(2, previews.Length);
            Assert.All(previews, preview => Assert.Same(expected.map, preview.Battle));
            fixture.Service.PreviewBattleMap(beta);
            Assert.Same(first, betaNotices.OfType<BattleMapPreview>().Last().Battle);
            fixture.Service.Attack(alpha, -30001);
            Assert.Same(expected.map, notices.OfType<FightEntered>().Last().Encounter.Battle);
            fixture.Service.Join(alpha, expected.map.Id);
            var loaded = notices.OfType<FightLoaded>().Last().View;
            Assert.Same(expected.map, loaded.Encounter.Battle);
            Assert.All(loaded.Positions, position =>
                Assert.Contains(position.Cell, (IReadOnlySet<int>)expected.map.Board.Placements[position.Team]));
            if (expected.index != 0) fixture.Service.Ready(alpha, true);
            fixture.Service.Forfeit(alpha);
            fixture.Service.Join(alpha, 100);
        }
    }
    [Fact]
    public void AttackApproachesBeforeEnteringAndForfeitRestoresTheConfirmedPosition()
    {
        using var f = new MovementFixture(deferTimers: true, catalog: CombatWorld(), combat: world => Encounters(world));
        var (a, notices) = f.Join("Alpha");
        Assert.Single(notices.OfType<MapSnapshot>().Single().Encounters!);
        f.Service.Attack(a, -999); Assert.Null(a.Fight); Assert.Null(a.Moving);
        f.Service.Attack(a, -30001); var approach = a.Moving!;
        Assert.Null(a.Fight); Assert.Equal(new[] { 0,1,2,3,4 }, approach.Path.Cells);
        f.Clock.Advance(TimeSpan.FromSeconds(4)); f.Service.Advance(a, approach);
        Assert.NotNull(a.Fight); Assert.False(a.MapReady); Assert.Equal(4, a.Location.CellId);
        Assert.Single(notices.OfType<FightEntered>());
        Assert.Throws<InvalidDataException>(() => f.Service.Join(a, 100));
        f.Service.Join(a, 200); Assert.Single(notices.OfType<FightLoaded>());
        f.Service.Place(a, a.Character.Id + 1, 1); Assert.Empty(notices.OfType<FightPositions>());
        f.Service.Place(a, a.Character.Id, 1);
        Assert.Equal(1, a.Fight.Preparation.Fighters.Single(p => p.Controlled).Cell);
        f.Service.Place(a, a.Character.Id, 5);
        Assert.Equal(1, a.Fight.Preparation.Fighters.Single(p => p.Controlled).Cell);
        f.Service.Ready(a, true); Assert.True(a.Fight.Started); Assert.Single(notices.OfType<FightStarted>());
        f.Service.Ready(a, true); Assert.Single(notices.OfType<FightStarted>());
        f.Service.Forfeit(a); Assert.Null(a.Fight); Assert.Single(notices.OfType<FightLeft>());
        f.Service.Advance(a, approach); f.Service.Join(a, 100);
        Assert.Single(notices.OfType<MapSnapshot>().Last().Encounters!);
        Assert.Equal(new WorldLocation(100, 4, 0, 2), f.Locations.LoadOrCreate(a.Profile, 290, a.Character.Id, f.Catalog.Initial, f.Catalog.Validate));
    }
    [Fact]
    public void CancellingApproachAndDisconnectingDoNotLeaveAnEncounterClaimed()
    {
        using var f = new MovementFixture(deferTimers: true, catalog: CombatWorld(), combat: world => Encounters(world));
        var (a, notices) = f.Join("Alpha"); f.Service.Attack(a, -30001); var old = a.Moving!;
        f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Service.Cancel(a, 1);
        f.Clock.Advance(TimeSpan.FromSeconds(5)); f.Service.Advance(a, old); Assert.Null(a.Fight);
        f.Service.Attack(a, -30001); f.Clock.Advance(TimeSpan.FromSeconds(3)); f.Service.Advance(a, a.Moving!);
        Assert.NotNull(a.Fight); a.Dispose();
        List<WorldNotice> restoredNotices = [];
        using var restored = f.Service.Select(a.Profile, a.Account, new object(), a.Character.Id, restoredNotices.Add)!;
        restored.ContextCreated = true; f.Service.Join(restored, 100);
        Assert.Equal(4, restored.Location.CellId); Assert.Single(restoredNotices.OfType<MapSnapshot>().Single().Encounters!);
        f.Service.Attack(restored, -30001); Assert.NotNull(restored.Fight);
        a.Dispose(); Assert.NotNull(restored.Fight); // Old cleanup cannot release the replacement.
    }
    [Fact]
    public void AnApproachAlreadyCoveredByPredictionTimeStillEntersTheFight()
    {
        var world = new WorldEntryCatalog(new(100, 0, 3, 1), new MovementPolicy(14, 560, 50) { PredictionLeadMilliseconds = 50 }, [],
            new EntryMap(100, 25, Enumerable.Range(0, 6).ToHashSet()) { Battles = [new(200, 26, CombatPreparationTests.Board())] });
        using var f = new MovementFixture(catalog: world, combat: catalog => Encounters(catalog, 2));
        var (a, _) = f.Join("Alpha"); f.Service.Attack(a, -30001);
        Assert.NotNull(a.Fight); Assert.Equal(1, a.Location.CellId); Assert.Null(a.PendingAttack);
    }
    [Fact]
    public void AnAttackQueuedDuringAnotherWalkApproachesAfterThatWalkFinishes()
    {
        using var f = new MovementFixture(deferTimers: true, catalog: CombatWorld(), combat: world => Encounters(world));
        var (a, _) = f.Join("Alpha");
        f.Service.Move(a, new(100, [0,1], 0)); var first = a.Moving!;
        f.Service.Attack(a, -30001);
        f.Clock.Advance(TimeSpan.FromSeconds(1)); f.Service.Advance(a, first);
        Assert.Null(a.Fight); Assert.NotNull(a.Moving); Assert.Equal(new[] { 1,2,3,4 }, a.Moving.Path.Cells);
        f.Clock.Advance(TimeSpan.FromSeconds(3)); f.Service.Advance(a, a.Moving);
        Assert.NotNull(a.Fight); Assert.Equal(4, a.Location.CellId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttackWaitsForCarriedTravelTimeAndDisconnectCancelsThatWait(bool disconnect)
    {
        var world = new WorldEntryCatalog(new(100, 0, 3, 1), new MovementPolicy(14, 560, 100) { CancellationLeadSteps = 1 }, [],
            new EntryMap(100, 25, Enumerable.Range(0, 6).ToHashSet()) { Battles = [new(200, 26, CombatPreparationTests.Board())] });
        using var f = new MovementFixture(catalog: world, combat: catalog => Encounters(catalog, 1));
        var (a, _) = f.Join("Alpha");
        f.Service.Move(a, new(100, [0,3], 0)); f.Clock.Advance(TimeSpan.FromMilliseconds(60)); f.Service.Cancel(a, 1);
        f.Service.Attack(a, -30001); Assert.Null(a.Fight); Assert.False(a.AttackWait.IsCompleted);
        if (disconnect) a.Dispose(); else f.Clock.Advance(TimeSpan.FromMilliseconds(40));
        await a.AttackWait.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        if (disconnect) Assert.Null(a.Fight); else Assert.NotNull(a.Fight);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TcpPreparationPlacementReadinessAndForfeitRestoreRoleplay(bool multipleMonsters)
    {
        var root = Path.Combine(Path.GetTempPath(), "combat-tcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var profiles = new ProfileStore(root); var profile = profiles.Create("Alpha");
            var store = new CharacterStore(root); var id = CreateCharacter(store, profile.Id, "Hero");
            var catalog = CombatWorld(); var locations = new LocationStore(root);
            var encounter = Encounters(catalog, 1).Encounters.Single();
            if (multipleMonsters) encounter = encounter with
            {
                Companions = [new(-2, 4, encounter.Monster with { Definition = 8, Grade = 2, Level = 3, Look = new(10, [], [], [110]) })]
            };
            var service = new WorldEntryService(new(store, SyntheticCatalog()), locations, catalog, combat: new(catalog, encounter));
            await using var host = new LocalSessionHost(characters: new(store, SyntheticCatalog()), worldEntry: service);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15)); var token = timeout.Token;
            using var session = host.Register(profile, "pt", _ => { });
            using var login = new TcpClient(); await Login(host, login, session.IssueTicket(), token);
            using var world = await Enter(host, login, token, emptyRoster: false);
            var snapshot = await EnterMap(world, id, 100, token);
            Assert.Contains(unchecked((ulong)-30001L), ActorIds(snapshot));
            await Send(world, "jxa", [], token);
            var preview = await Event(world, "jvk", token);
            Assert.Equal(100UL, preview.UInt64(1));
            Assert.Equal(200UL, preview.UInt64(4));
            var previewZones = ProtoObject.Read(preview.Bytes(2), 128);
            Assert.Equal(new byte[] { 3,4,5 }, previewZones.Bytes(1));
            Assert.Equal(new byte[] { 0,1,2 }, previewZones.Bytes(2));
            await Send(world, "hms", WorldProtocol.Number(2, unchecked((ulong)-30001L)), token);
            Assert.Equal(unchecked((ulong)-30001L), (await Event(world, "kjz", token)).UInt64(1));
            await Event(world, "kjy", token); Assert.Equal(1UL, (await Event(world, "kjp", token)).UInt64(1));
            var joined = await Event(world, "jxi", token);
            Assert.Equal(4UL, joined.UInt64(6));
            Assert.Equal(200UL, (await Event(world, "jow", token)).UInt64(1));
            await Send(world, "kjt", WorldProtocol.Number(1, 200), token);
            var map = await Event(world, "jnj", token); Assert.True(map.Has(11)); Assert.Equal(200UL, map.UInt64(10));
            Assert.Equal(26UL, map.UInt64(12));
            var reference = await Event(world, "jnv", token);
            Assert.Equal(200UL, reference.UInt64(3)); Assert.Equal(26UL, reference.UInt64(5));
            Assert.Equal(1UL, (await Event(world, "jww", token)).UInt64(4));
            var player = ProtoObject.Read((await Event(world, "jwj", token)).Bytes(1), 128);
            Assert.Equal((ulong)id, player.UInt64(1)); Assert.Equal(2UL, ProtoObject.Read(player.Bytes(2), 128).UInt64(3));
            var enemy = ProtoObject.Read((await Event(world, "jwj", token)).Bytes(1), 128);
            Assert.Equal(ulong.MaxValue, enemy.UInt64(1));
            var fightDetails = ProtoObject.Read(ProtoObject.Read(enemy.Bytes(3), 128).Bytes(4), 128);
            var identity = ProtoObject.Read(ProtoObject.Read(fightDetails.Bytes(6), 128).Bytes(3), 128);
            Assert.Equal(7UL, identity.UInt64(3)); Assert.Equal(1UL, identity.UInt64(1)); Assert.Equal(1UL, identity.UInt64(2));
            if (multipleMonsters)
            {
                var companion = ProtoObject.Read((await Event(world, "jwj", token)).Bytes(1), 128);
                Assert.Equal(unchecked((ulong)-2L), companion.UInt64(1));
                Assert.Equal(4UL, ProtoObject.Read(companion.Bytes(2), 128).UInt64(3));
                var companionDetails = ProtoObject.Read(companion.Bytes(3), 128);
                Assert.Equal(10UL, ProtoObject.Read(companionDetails.Bytes(2), 128).UInt64(1));
                var companionIdentity = ProtoObject.Read(ProtoObject.Read(ProtoObject.Read(companionDetails.Bytes(4), 128).Bytes(6), 128).Bytes(3), 128);
                Assert.Equal(8UL, companionIdentity.UInt64(3)); Assert.Equal(2UL, companionIdentity.UInt64(1)); Assert.Equal(3UL, companionIdentity.UInt64(2));
            }
            Assert.Equal(preview.Bytes(2), (await Event(world, "jxk", token)).Bytes(1));
            Assert.Equal((ulong)id, (await Event(world, "juf", token)).UInt64(6));
            var teamUpdate = await Event(world, "jxh", token);
            Assert.Equal(joined.UInt64(1), teamUpdate.UInt64(1));
            var team = ProtoObject.Read(teamUpdate.Bytes(2), 128);
            Assert.Equal(0UL, team.UInt64(6, false)); Assert.Equal((ulong)id, team.UInt64(8));
            var members = ProtoObject.Read(team.Bytes(4), 128).Repeated(2);
            var member = ProtoObject.Read(Assert.Single(members).Bytes, 128);
            Assert.Equal((ulong)id, member.UInt64(3));
            var memberIdentity = ProtoObject.Read(member.Bytes(5), 128);
            Assert.Equal("Hero", memberIdentity.Text(2)); Assert.Equal(1UL, memberIdentity.UInt64(3));
            await Send(world, "jxb", WorldProtocol.Join(WorldProtocol.Number(1, 1), WorldProtocol.Number(2, (ulong)id)), token);
            var position = (await Event(world, "jws", token)).Repeated(2).Select(p => ProtoObject.Read(p.Bytes, 128)).Single(p => p.UInt64(2) == (ulong)id);
            Assert.Equal(1UL, position.UInt64(3));
            await Send(world, "jxj", Convert.FromHexString("0801"), token); await Event(world, "jxr", token); await Event(world, "jvu", token);
            await Send(world, "kjx", [], token); await Event(world, "kjy", token);
            Assert.Equal(0UL, (await Event(world, "kjp", token)).UInt64(1));
            Assert.Equal(100UL, (await Event(world, "jow", token)).UInt64(1));
            Assert.Contains(unchecked((ulong)-30001L), ActorIds(await LoadedMap(world, 100, token)));
            Assert.Equal(catalog.Initial, locations.LoadOrCreate(profile.Id, 290, id, catalog.Initial, catalog.Validate));
        }
        finally { Directory.Delete(root, true); }
    }
}
