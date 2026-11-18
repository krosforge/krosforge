using System.Buffers;
using Krosforge.Runtime.Native.Protocol;
using Krosforge.Runtime.Native.Protocol.Protobuf;
namespace Krosforge.Runtime.Native;

internal sealed record MovementRequest(long Map, int[] TurningCells, int Direction);
internal static class WorldMovementWire
{
    internal static MovementRequest Read(byte[] payload)
    {
        var body = ProtoObject.Read(payload, 600); body.Only(1, 2, 3);
        if (body.UInt64(3, false) != 0) throw new ProtocolException("Unsupported movement mode.");
        var map = body.UInt64(2);
        if (map is 0 or > long.MaxValue) throw new ProtocolException("Invalid movement map.");
        List<int> keys = [];
        foreach (var field in body.Repeated(1))
        {
            if (field.WireType == 0) Add(field.Number);
            else if (field.WireType == 2)
            {
                ReadOnlySpan<byte> packed = field.Bytes;
                while (!packed.IsEmpty) Add(ReadVarint(ref packed));
            }
            else throw new ProtocolException("Invalid movement keys.");
        }
        if (keys.Count < 2) throw new ProtocolException("Movement needs two points.");
        return new((long)map, keys.Select(key => key & 4095).ToArray(), keys[^1] >> 12);
        void Add(ulong key)
        {
            if (key > 32767 || keys.Count == 560) throw new ProtocolException("Movement key budget exceeded.");
            keys.Add((int)key);
        }
    }
    internal static void Acknowledgement(byte[] payload)
    {
        var body = ProtoObject.Read(payload, 128); body.Only(1, 2);
        if (body.UInt64(1, false) != 0 || body.UInt64(2, false) != 0) throw new ProtocolException("Unsupported movement acknowledgement.");
    }
    internal static int Cancellation(byte[] payload)
    {
        var body = ProtoObject.Read(payload, 128); body.Only(1);
        var cell = body.UInt64(1, false);
        return cell <= 559 ? (int)cell : throw new ProtocolException("Invalid cancellation cell.");
    }
    internal static long ChangeMap(byte[] payload)
    {
        var body = ProtoObject.Read(payload, 128); body.Only(1, 2);
        if (body.UInt64(1, false) != 0) throw new ProtocolException("Unsupported map change mode.");
        var map = body.UInt64(2);
        return map is > 0 and <= int.MaxValue ? (long)map : throw new ProtocolException("Invalid destination map.");
    }
    internal static byte[] Movement(long actor, IReadOnlyList<int> cells)
    {
        var packed = new ArrayBufferWriter<byte>(); foreach (var cell in cells) ProtoWriter.Varint(packed, checked((ulong)cell));
        return WorldProtocol.Join(WorldProtocol.Bytes(1, packed.WrittenSpan.ToArray()), WorldProtocol.Number(4, checked((ulong)actor)));
    }
    internal static byte[] Correction(long actor, int cell) => WorldProtocol.Join(WorldProtocol.Number(1, checked((ulong)cell)), WorldProtocol.Number(2, checked((ulong)actor)));
    private static ulong ReadVarint(ref ReadOnlySpan<byte> bytes)
    {
        ulong result = 0;
        for (var i = 0; i < 10; i++)
        {
            if (bytes.IsEmpty) throw new ProtocolException("Truncated movement key.");
            var next = bytes[0]; bytes = bytes[1..];
            if (i == 9 && next > 1) throw new ProtocolException("Movement key overflow.");
            result |= (ulong)(next & 127) << (i * 7);
            if ((next & 128) == 0) return result;
        }
        throw new ProtocolException("Unterminated movement key.");
    }
}
