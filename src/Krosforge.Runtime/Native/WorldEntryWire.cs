using System.Text;
using Krosforge.Runtime.Native.Protocol;
using Krosforge.Runtime.Native.Protocol.Protobuf;
namespace Krosforge.Runtime.Native;

// Field tags verified against 3.7.14.18 metadata. See the independent wire fixture.
internal static class WorldEntryWire
{
    private static byte[] N(int tag, long value) => WorldProtocol.Number(tag, checked((ulong)value));
    private static byte[] B(int tag, byte[] value) => WorldProtocol.Bytes(tag, value);
    private static byte[] J(params byte[][] values) => WorldProtocol.Join(values);
    internal static void ReadContext(byte[] payload)
    {
        var body = ProtoObject.Read(payload, 128); body.Only(1);
        if (body.UInt64(1, false) != 0) throw new ProtocolException("Unsupported world context.");
    }
    internal static long ReadMap(byte[] payload)
    {
        var body = ProtoObject.Read(payload, 128); body.Only(1);
        var map = body.UInt64(1);
        return map is > 0 and <= long.MaxValue ? (long)map : throw new ProtocolException("Invalid map identity.");
    }
    internal static byte[] Context() => N(1, 0);
    internal static byte[] LoadMap(long map) => N(1, map);
    internal static byte[] Reference(EntryMap map) => J(N(3, map.Id), N(5, map.Subarea));
    internal static byte[] EmptyInventory(long currency = 0) => J(B(3, []), N(4, currency));
    internal static byte[] Statistics(WorldEntryService.Selection selection) => Statistics(selection.Attributes, selection.Character.Level);
    internal static byte[] Statistics(EntryAttributes attributes, int level)
    {
        byte[] Simple(int id, long value) => B(1, J(N(2, id), B(3, N(2, value))));
        byte[] Usable(int id, int value) => B(1, J(N(2, id), B(1, J(N(4, value), N(7, 0)))));
        // StatId names recovered from the current metadata: HitPoints, MaxLifePoints,
        // Shield, HitPointLoss, CurPermanentDamage, Level, ActionPoints, MovementPoints.
        var stats = J(Usable(0, attributes.Life), Simple(95, attributes.Life), Simple(96, 0), Simple(97, 0),
            Simple(5, level), Usable(3, 0), Usable(11, 0), Simple(102, 0),
            Usable(1, attributes.ActionPoints), Usable(23, attributes.MovementPoints),
            Simple(29, attributes.Energy), Simple(48, attributes.Prospecting),
            N(11, attributes.Experience), N(8, attributes.ExperienceFloor), N(2, attributes.NextLevelExperience), B(10, []));
        return B(1, stats);
    }
    internal static byte[] Information(WorldEntryService.Selection selection)
    {
        return Information(selection.Map, [new(selection.Character, selection.Account, selection.Location)]);
    }
    internal static byte[] Information(EntryMap map, IEnumerable<WorldActor> actors, IEnumerable<CombatEncounter>? encounters = null) =>
        J(actors.Select(actor => B(3, Actor(actor))).Concat((encounters ?? []).Select(e => B(3, CombatWire.Group(e))))
            .Concat([N(10, map.Id), N(12, map.Subarea)]).ToArray());
    internal static byte[] Actor(WorldActor value)
    {
        var character = value.Character;
        var player = J(N(2, value.Account), N(7, character.Gender), B(6, []));
        var humanoid = J(B(1, player), B(5, Encoding.UTF8.GetBytes(character.Name)));
        var details = J(B(1, B(3, humanoid)), B(2, new CharacterWireCodec().Look(character.Look)));
        var position = J(N(3, value.Location.CellId), N(4, value.Location.Direction));
        return J(N(1, character.Id), B(2, position), B(3, details));
    }
    internal static byte[][] Events(WorldNotice notice) => notice switch
    {
        MapSnapshot snapshot => [.. snapshot.NeutralCalendar is { } calendar ? new[] { WorldProtocol.Event("lov", N(3, calendar)) } : Array.Empty<byte[]>(), WorldProtocol.Event("jnj", Information(snapshot.Map, snapshot.Actors, snapshot.Encounters)), WorldProtocol.Event("jnv", Reference(snapshot.Map))],
        ActorAppeared appeared => [WorldProtocol.Event("jns", B(1, Actor(appeared.Actor)))],
        ActorDeparted departed => [WorldProtocol.Event("kjz", N(1, departed.Id))],
        ActorMoved moved => [WorldProtocol.Event("jnz", WorldMovementWire.Movement(moved.Id, moved.Cells))],
        ActorCorrected corrected => [WorldProtocol.Event("joz", WorldMovementWire.Correction(corrected.Id, corrected.Cell))],
        MovementReleased => [WorldProtocol.Event("jpk", [])],
        MapLoading loading => [WorldProtocol.Event("jow", LoadMap(loading.Map))],
        _ => CombatWire.Events(notice)
    };
}
