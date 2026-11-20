using Krosforge.Runtime.Native.Protocol;
using Krosforge.Runtime.Native.Protocol.Protobuf;
namespace Krosforge.Runtime.Native;

internal static class SpellShortcutWire
{
    private const int SpellBar = 1;
    internal static bool IsSpellBar(ProtoObject body) => body.UInt64(1, false) == SpellBar;

    internal static SpellShortcut ReadAssignment(ProtoObject body)
    {
        body.Only(1, 2);
        var shortcut = ProtoObject.Read(body.Bytes(2), 3);
        shortcut.Only(1, 2, 3);
        if (shortcut.UInt64(1, false) != 0) throw new ProtocolException("Unsupported spell shortcut type.");
        var slot = ReadSlot(shortcut, 3);
        var spell = ProtoObject.Read(shortcut.Bytes(2), 1);
        spell.Only(1);
        var id = spell.UInt64(1, false);
        if (id > int.MaxValue) throw new ProtocolException("Invalid spell shortcut.");
        return new(slot, (int)id);
    }

    internal static int ReadRemoval(ProtoObject body)
    {
        body.Only(1, 2);
        return ReadSlot(body, 2);
    }

    internal static (int Source, int Destination) ReadMove(ProtoObject body)
    {
        body.Only(1, 2, 3, 4);
        if (body.UInt64(4, false) != 0) throw new ProtocolException("Unsupported spell bar owner.");
        // The move message writes the receiving slot before the dragged slot.
        return (ReadSlot(body, 3), ReadSlot(body, 2));
    }

    private static int ReadSlot(ProtoObject value, int field)
    {
        var slot = value.UInt64(field, false);
        return slot <= (ulong)short.MaxValue ? (int)slot : throw new ProtocolException("Invalid spell shortcut slot.");
    }

    internal static byte[] Encode(SpellShortcut row) => WorldProtocol.Join(WorldProtocol.Number(3, (ulong)row.Slot),
        WorldProtocol.Bytes(2, WorldProtocol.Number(1, (ulong)row.Spell)));

    internal static byte[] Snapshot(IReadOnlyList<SpellShortcut> rows) => WorldProtocol.Join(
        rows.Select(row => WorldProtocol.Bytes(1, Encode(row))).Append(WorldProtocol.Number(2, SpellBar)).ToArray());
}
