using System.Buffers;
using System.Text;
using Krosforge.Core;
using Krosforge.Runtime.Native.Protocol;
using Krosforge.Runtime.Native.Protocol.Protobuf;
namespace Krosforge.Runtime.Native;

// Compatibility fields independently checked in build 3.7.14.18 metadata.
internal static partial class CombatWire
{
    private static byte[] N(int tag, long value) => WorldProtocol.Number(tag, unchecked((ulong)value));
    private static byte[] B(int tag, byte[] value) => WorldProtocol.Bytes(tag, value);
    private static byte[] J(params byte[][] values) => WorldProtocol.Join(values);
    private static byte[] E(string type, byte[] value) => WorldProtocol.Event(type, value);
    private static byte[] Packed(int tag, IEnumerable<int> values)
    {
        var w = new ArrayBufferWriter<byte>(); foreach (var value in values) ProtoWriter.Varint(w, checked((ulong)value));
        return B(tag, w.WrittenSpan.ToArray());
    }
    internal static long Attack(byte[] payload)
    {
        var body = ProtoObject.Read(payload, 128); body.Only(2);
        var group = unchecked((long)body.UInt64(2));
        return group < -1 ? group : throw new ProtocolException("Invalid monster group.");
    }
    internal static (long Actor, int Cell) Placement(byte[] payload)
    {
        var body = ProtoObject.Read(payload, 128); body.Only(1, 2);
        var cell = body.UInt64(1, false); var actor = body.UInt64(2);
        if (cell >= 560 || actor is 0 or > long.MaxValue) throw new ProtocolException("Invalid combat placement.");
        return ((long)actor, (int)cell);
    }
    internal static bool Ready(byte[] payload)
    {
        var body = ProtoObject.Read(payload, 128); body.Only(1, 2);
        var ready = body.UInt64(1, false);
        if (ready > 1 || body.UInt64(2, false) != 0) throw new ProtocolException("Unsupported readiness mode.");
        return ready == 1;
    }
    internal static byte[] Group(CombatEncounter e)
    {
        var m = e.Monster;
        byte[] Identity(CombatMonster monster) => J(N(5, monster.Definition), B(3, new CharacterWireCodec().Look(monster.Look)),
            N(2, monster.Grade), N(4, monster.Level));
        var group = J(new[] { B(4, Identity(m)) }.Concat(e.Companions.Select(other => B(3, Identity(other.Monster)))).ToArray());
        var roleplay = B(5, B(6, group));
        return J(N(1, e.GroupId), B(2, J(N(3, e.GroupCell), N(4, e.Direction))),
            B(3, J(B(1, roleplay), B(2, new CharacterWireCodec().Look(m.Look)))));
    }
    internal static byte[] Fighter(CombatView view, CombatPlacement position)
    {
        var player = position.Controlled; var monster = player ? view.Encounter.Monster : view.Encounter.MonsterFor(position.ActorId); var character = view.Character;
        var life = player ? view.Attributes.Life : monster.Life;
        var ap = player ? view.Attributes.ActionPoints : monster.ActionPoints;
        var mp = player ? view.Attributes.MovementPoints : monster.MovementPoints;
        var level = player ? character.Level : monster.Level;
        byte[] Simple(int id, int value) => B(4, J(N(2, id), B(3, N(2, value))));
        byte[] Usable(int id, int value) => B(4, J(N(2, id), B(1, J(N(4, value), N(7, 0)))));
        var common = J(N(1, 2), Usable(0, life), Simple(95, life), Simple(96, 0), Simple(97, 0), Simple(102, 0),
            Simple(5, level), Usable(1, ap), Usable(23, mp),
            J(Attributes(player ? CombatStatistics.Empty : monster.Statistics).Select(entry => B(4, entry)).ToArray()));
        var identity = player
            ? B(5, J(N(2, level), B(4, Encoding.UTF8.GetBytes(character.Name)), B(1, J(N(2, character.BreedId), N(4, character.Gender), N(6, level)))))
            : B(6, B(3, J(N(1, monster.Grade), N(2, level), N(3, monster.Definition))));
        var fight = J(B(7, common), B(8, J(N(1, 1), N(2, position.Team))), identity);
        return J(N(1, position.ActorId), B(2, J(N(3, position.Cell), N(4, view.Encounter.Direction))),
            B(3, J(B(2, new CharacterWireCodec().Look(player ? character.Look : monster.Look)), B(4, fight))));
    }
    private static byte[] Zones(CombatBoard board) => J(Packed(1, board.Placements[1].Order()), Packed(2, board.Placements[0].Order()));
    private static byte[] ControlledTeam(CombatView view)
    {
        var player = view.Positions.Single(p => p.Controlled);
        var member = J(N(3, player.ActorId), B(5, J(B(2, Encoding.UTF8.GetBytes(view.Character.Name)), N(3, view.Character.Level))));
        return J(N(1, checked((int)view.Id)), B(2, J(N(6, player.Team), N(8, player.ActorId), B(4, B(2, member)))));
    }
    private static byte[][] Load(CombatView view) =>
    [
        E("jnj", J(N(10, view.Encounter.Battle.Id), N(12, view.Encounter.Battle.Subarea), B(11, J(N(1, 1), N(3, 1))))),
        E("jnv", WorldEntryWire.Reference(view.Encounter.Battle.Id, view.Encounter.Battle.Subarea)),
        E("jww", J(N(2, 1), N(4, 1), N(5, view.Encounter.FightType))),
        .. view.Positions.OrderByDescending(p => p.Controlled).ThenByDescending(p => p.ActorId).Select(p => E("jwj", B(1, Fighter(view, p)))),
        E("jxk", J(B(1, Zones(view.Encounter.Board)), N(2, 0))),
        E("juf", Book(view)),
        E("jxh", ControlledTeam(view))
    ];
    internal static byte[][] Events(WorldNotice notice) => notice switch
    {
        EncounterAppeared shown => [E("jns", B(1, Group(shown.Encounter)))],
        EncounterDeparted gone => [E("kjz", N(1, gone.Group))],
        BattleMapPreview preview => [E("jvk", J(N(1, preview.Source), B(2, Zones(preview.Battle.Board)), N(4, preview.Battle.Id)))],
        FightEntered enter => [E("kjy", []), E("kjp", N(1, 1)), E("jxi", J(N(1, enter.Id), N(6, enter.Encounter.FightType))), E("jow", N(1, enter.Encounter.Battle.Id))],
        FightLoaded loaded => Load(loaded.View),
        FightPositions changed => [E("jws", J(changed.Positions.Select(p => B(2, J(N(2, p.ActorId), N(3, p.Cell)))).ToArray()))],
        FightStarted started => [E("jxr", []), E("jvu", J(started.Order.Select(id => B(1, B(2, J(N(1, id), B(3, []))))).ToArray()))],
        FightTurn turn => TurnEvents(turn),
        FightAction action => ActionEvents(action),
        FightTurnEnding => [E("jug", [])],
        FightActionRejected => [],
        FightCompleted completed => ResultEvents(completed),
        FightLeft left => [E("kjy", []), E("kjp", WorldEntryWire.Context()), E("jow", N(1, left.Map))],
        _ => throw new ArgumentOutOfRangeException(nameof(notice))
    };
}
