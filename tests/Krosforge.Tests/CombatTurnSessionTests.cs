using System.Collections.Immutable;
using System.Net.Sockets;
using Krosforge.Core;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Krosforge.Runtime.Native.Protocol;
using Krosforge.Runtime.Native.Protocol.Protobuf;
using Xunit;
namespace Krosforge.Tests;

public sealed partial class WorldSessionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TcpCombatMovementCastAndAutomaticReplyUseAcknowledgedSequences(bool monsterApproaches)
    {
        var root = Path.Combine(Path.GetTempPath(), "combat-turn-tcp-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var profiles = new ProfileStore(root); var profile = profiles.Create("Alpha");
            var store = new CharacterStore(root); var id = CreateCharacter(store, profile.Id, "Hero");
            var catalog = TurnWorld(); var locations = new LocationStore(root);
            var service = new WorldEntryService(new(store, SyntheticCatalog()), locations, catalog, combat: TurnEncounter(catalog), spells: TurnSpells(monsterApproaches ? 1 : 8));
            await using var host = new LocalSessionHost(characters: new(store, SyntheticCatalog()), worldEntry: service);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15)); var token = timeout.Token;
            using var session = host.Register(profile, "pt", _ => { });
            using var login = new TcpClient(); await Login(host, login, session.IssueTicket(), token);
            using var world = await Enter(host, login, token, emptyRoster: false);
            await EnterMap(world, id, 100, token);
            await Send(world, "hms", WorldProtocol.Number(2, unchecked((ulong)-30001L)), token);
            foreach (var type in new[] { "kjz", "kjy", "kjp", "jxi", "jow" }) await Event(world, type, token);
            await Send(world, "kjt", WorldProtocol.Number(1, 200), token);
            foreach (var type in new[] { "jnj", "jnv", "jww", "jwj", "jwj", "jxk" }) await Event(world, type, token);
            var book = await Event(world, "juf", token);
            await Event(world, "jxh", token);
            Assert.Equal(1UL, ProtoObject.Read(Assert.Single(book.Repeated(5)).Bytes, 128).UInt64(2));
            await Send(world, "jxj", [8, 1], token);
            foreach (var type in new[] { "jxr", "jvu", "krg", "jvi" }) await Event(world, type, token);
            Assert.Equal((ulong)id, (await Event(world, "jvj", token)).UInt64(3));
            // Independent fixtures: 100 -> 86 retreats from the monster; 100 -> 114 approaches it.
            await Send(world, "jok", Convert.FromHexString(monsterApproaches ? "0a02645610c801" : "0a02647210c801"), token);
            await Event(world, "jqh", token);
            Assert.Equal(new byte[] { 100, monsterApproaches ? (byte)86 : (byte)114 }, (await Event(world, "jnz", token)).Bytes(1));
            Assert.Equal(129UL, (await Event(world, "jsq", token)).UInt64(22));
            var sequence = (await Event(world, "jsy", token)).UInt64(2); await Event(world, "krg", token);
            await Send(world, "jst", WorldProtocol.Join(WorldProtocol.Number(2, sequence), WorldProtocol.Number(3, 1)), token);
            await Send(world, "jsr", Convert.FromHexString("0801108001"), token);
            await Event(world, "jqh", token);
            Assert.Equal(300UL, (await Event(world, "jsq", token)).UInt64(22));
            Assert.Equal(102UL, (await Event(world, "jsq", token)).UInt64(22));
            var damage = await Event(world, "jsq", token);
            Assert.Equal(2UL, ProtoObject.Read(damage.Bytes(37), 128).UInt64(6));
            var castSequence = (await Event(world, "jsy", token)).UInt64(2); Assert.True(castSequence > sequence);
            await Event(world, "krg", token);
            await Send(world, "jst", WorldProtocol.Join(WorldProtocol.Number(2, castSequence), WorldProtocol.Number(3, 1)), token);
            await Send(world, "jwo", [], token); await Event(world, "jug", token);
            await Send(world, "jtd", [8, 1], token);
            await Event(world, "krg", token); await Event(world, "jvi", token);
            Assert.Equal(ulong.MaxValue, (await Event(world, "jvj", token)).UInt64(3)); await Event(world, "jte", token);
            Assert.Equal(ulong.MaxValue, (await Event(world, "jqh", token)).UInt64(2));
            if (monsterApproaches)
            {
                var walk = await Event(world, "jnz", token);
                Assert.Equal(ulong.MaxValue, walk.UInt64(4));
                Assert.NotEmpty(walk.Bytes(1));
                Assert.Equal(129UL, (await Event(world, "jsq", token)).UInt64(22));
                var walkSequence = await Event(world, "jsy", token);
                Assert.Equal((ulong)id, walkSequence.UInt64(4));
                await Event(world, "krg", token);
                await Send(world, "jst", WorldProtocol.Join(WorldProtocol.Number(2, walkSequence.UInt64(2)), WorldProtocol.Number(3, 1)), token);
                Assert.Equal(ulong.MaxValue, (await Event(world, "jqh", token)).UInt64(2));
            }
            foreach (var type in new[] { "jsq", "jsq", "jsq" }) await Event(world, type, token);
            var automatic = await Event(world, "jsy", token); Assert.Equal((ulong)id, automatic.UInt64(4));
            await Event(world, "krg", token);
            await Send(world, "kjx", [], token);
            foreach (var type in new[] { "kjy", "kjp", "jow" }) await Event(world, type, token);
            await LoadedMap(world, 100, token);
            Assert.Equal(catalog.Initial, locations.LoadOrCreate(profile.Id, 290, id, catalog.Initial, catalog.Validate));
        }
        finally { Directory.Delete(root, true); }
    }
    private static CombatBoard TurnBoard() => new(14, Enumerable.Range(0, 560)
        .Select(id => new CombatCell(id, true, true, 0, 1, id == 100 ? 0 : id == 128 ? 1 : null)));
    private static WorldEntryCatalog TurnWorld() => new(new(100, 0, 3, 1), new(14, 560, 50), [],
        new EntryMap(100, 25, Enumerable.Range(0, 560).ToHashSet()) { Battle = new(200, 26, TurnBoard()) });
    private static CombatCatalog TurnEncounter(WorldEntryCatalog world) => new(world,
        new CombatEncounter(100, -30001, 1, 3, 2, 4, 100, 128,
            new(7, 1, 1, 15, 2, 3, new(9, [], [], [100])) { Spells = [2] }, world.Map(100).Battle!));
    private static CombatSpellCatalog TurnSpells(int monsterRange = 8)
    {
        CombatSpell spell = new(1, 1, 1, 2, 1, 8, true, true, 0, new(CombatElement.Fire, 2, 2), null);
        return new([spell], monsters: new Dictionary<int, ImmutableArray<CombatSpell>> { [2] = [spell with { Id = 2, MaximumRange = monsterRange }] });
    }
    private static void Fight(MovementFixture fixture, WorldEntryService.Selection selection)
    {
        fixture.Service.Attack(selection, -30001); fixture.Service.Join(selection, 200); fixture.Service.Ready(selection, true);
        Assert.NotNull(selection.Fight!.Turns);
    }
    [Fact]
    public void AnimationAckGatesActionsAndOldFightAckCannotUnlockNewFight()
    {
        using var f = new MovementFixture(deferTimers: true, catalog: TurnWorld(), combat: TurnEncounter, spells: TurnSpells());
        var (a, notices) = f.Join("Alpha"); Fight(f, a);
        f.Service.CombatMove(a, new(100, [100, 114], 3));
        Assert.Single(notices.OfType<FightActionRejected>());
        f.Service.CombatMove(a, new(200, [100, 114], 3));
        var first = Assert.Single(notices.OfType<FightAction>());
        Assert.IsType<CombatWalk>(first.Action); Assert.Equal(2, first.Player.Mp);
        f.Service.Cast(a, 1, 128); Assert.Single(notices.OfType<FightAction>());
        f.Service.CombatAcknowledge(a, first.Sequence + 1); Assert.Equal(first.Sequence, a.Fight!.Sequence);
        f.Service.CombatAcknowledge(a, first.Sequence); Assert.Null(a.Fight.Sequence);
        f.Service.Cast(a, 1, 128); Assert.Equal(2, notices.OfType<FightAction>().Count());
        var old = a.Fight.Sequence!.Value; f.Service.Forfeit(a); f.Service.Join(a, 100); Fight(f, a);
        f.Service.Cast(a, 1, 128); var current = a.Fight!.Sequence;
        Assert.True(current > old); f.Service.CombatAcknowledge(a, old); Assert.Equal(current, a.Fight.Sequence);
        Assert.Equal(0, a.Location.CellId); // Combat movement never writes the exploration location.
    }
    [Fact]
    public void ExpiredTurnIsProcessedBeforeTheNextCastWithoutATimerContinuation()
    {
        using var f = new MovementFixture(deferTimers: true, catalog: TurnWorld(), combat: TurnEncounter, spells: TurnSpells());
        var (a, notices) = f.Join("Alpha"); Fight(f, a);
        f.Clock.Advance(TimeSpan.FromSeconds(31));
        f.Service.Cast(a, 1, 128);
        Assert.Empty(notices.OfType<FightAction>()); Assert.True(a.Fight!.AwaitingReady); Assert.Equal(6, a.Fight.Turns!.Current.Ap);
        f.Service.CombatTurnReady(a, true); Assert.Equal(-1, a.Fight.Turns.Current.Id);
        f.Clock.Advance(TimeSpan.FromMilliseconds(250)); f.Service.AdvanceCombat(a);
        var automatic = Assert.Single(notices.OfType<FightAction>());
        Assert.Equal(-1, automatic.Action.Actor); Assert.Equal(98, automatic.Player.Life);
        f.Service.CombatTurnReady(a, true); Assert.Equal(2, a.Fight.Turns.Serial);
    }
    [Fact]
    public void MissingAnimationAckExpiresConnectionAndReleasesEncounter()
    {
        using var f = new MovementFixture(deferTimers: true, catalog: TurnWorld(), combat: TurnEncounter, spells: TurnSpells());
        var (a, _) = f.Join("Alpha"); Fight(f, a); f.Service.Cast(a, 1, 128);
        f.Clock.Advance(TimeSpan.FromSeconds(16)); f.Service.CombatAcknowledge(a, a.Fight!.Sequence!.Value);
        Assert.Null(a.Fight);
        using var restored = f.Service.Select(a.Profile, a.Account, new object(), a.Character.Id)!;
        restored.ContextCreated = true; f.Service.Join(restored, 100); f.Service.Attack(restored, -30001);
        Assert.NotNull(restored.Fight); Assert.Equal(0, restored.Location.CellId);
    }
    [Fact]
    public void EndRequestDuringAnimationWaitsForAckAndReadinessBeforeAdvancing()
    {
        using var f = new MovementFixture(deferTimers: true, catalog: TurnWorld(), combat: TurnEncounter, spells: TurnSpells());
        var (a, notices) = f.Join("Alpha"); Fight(f, a); f.Service.Cast(a, 1, 128);
        var sequence = a.Fight!.Sequence!.Value;
        f.Service.EndCombatTurn(a); Assert.True(a.Fight.EndRequested); Assert.False(a.Fight.AwaitingReady);
        f.Service.CombatAcknowledge(a, sequence); Assert.True(a.Fight.AwaitingReady); Assert.Single(notices.OfType<FightTurnEnding>());
        f.Service.EndCombatTurn(a); Assert.Single(notices.OfType<FightTurnEnding>());
        f.Service.CombatTurnReady(a, true); Assert.Equal(-1, a.Fight.Turns!.Current.Id);
        f.Service.CombatAcknowledge(a, sequence); Assert.Equal(2, a.Fight.Turns.Serial);
    }
    [Fact]
    public void CombatInputAndActionEnvelopesMatchIndependentNativeFields()
    {
        Assert.Equal((13338, 114), CombatWire.CastRequest(Convert.FromHexString("089a681072")));
        Assert.Equal(7, CombatWire.SequenceAcknowledgement(Convert.FromHexString("10071801")));
        Assert.Throws<ProtocolException>(() => CombatWire.SequenceAcknowledgement(Convert.FromHexString("10071800")));
        CombatSpell spell = new(1, 12, 1, 2, 1, 8, true, true, 0, new(CombatElement.Fire, 2, 2), null);
        var player = new CombatFighter(5, 0, true, 100, 100, 100, 6, 4, 3, 3, CombatStatistics.Empty, [spell]);
        var events = CombatWire.Events(new FightAction(5, 7, new CombatCast(5, spell, 114, -1, false, 2, false), player, 1, 50));
        (string Type, ProtoObject Body) Read(byte[] bytes)
        {
            var any = ProtoObject.Read(ProtoObject.Read(ProtoObject.Read(bytes, 128).Bytes(3), 128).Bytes(1), 128);
            return (any.Text(1)["type.ankama.com/".Length..], ProtoObject.Read(any.Bytes(2, false), 128));
        }
        var monsterMovement = CombatWire.Events(new FightAction(5, 8, new CombatWalk(-1, new[] { 128, 114 }), player, 1, 50));
        var movement = Read(monsterMovement[1]);
        Assert.Equal("jnz", movement.Type); Assert.Equal(ulong.MaxValue, movement.Body.UInt64(4));
        Assert.Equal(new byte[] { 128, 1, 114 }, movement.Body.Bytes(1));
        var parsed = events.Select(Read).ToArray();
        Assert.Equal(new[] { "jqh", "jsq", "jsq", "jsq", "jsy", "krg" }, parsed.Select(p => p.Type));
        Assert.Equal(300UL, parsed[1].Body.UInt64(22));
        var cast = ProtoObject.Read(parsed[1].Body.Bytes(2), 128);
        Assert.Equal(114UL, cast.UInt64(5)); Assert.Equal(ulong.MaxValue, cast.UInt64(6));
        Assert.Equal(12UL, ProtoObject.Read(cast.Bytes(2), 128).UInt64(1));
        Assert.Equal(102UL, parsed[2].Body.UInt64(22));
        Assert.Equal(unchecked((ulong)-2L), ProtoObject.Read(parsed[2].Body.Bytes(13), 128).UInt64(2));
        Assert.Equal(99UL, parsed[3].Body.UInt64(22));
        var loss = ProtoObject.Read(parsed[3].Body.Bytes(37), 128);
        Assert.Equal(2UL, loss.UInt64(4)); Assert.Equal(2UL, loss.UInt64(6)); Assert.Equal(ulong.MaxValue, loss.UInt64(5));
        Assert.Equal(7UL, parsed[4].Body.UInt64(2)); Assert.Equal(5UL, parsed[4].Body.UInt64(4));
    }
}
