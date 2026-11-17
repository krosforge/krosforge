using System.Buffers;

namespace Krosforge.Runtime.Native.Protocol.Protobuf;

internal static class ProtoWriter
{
    internal static void Bytes(IBufferWriter<byte> writer, int field, ReadOnlySpan<byte> bytes)
    {
        Varint(writer, ((ulong)field << 3) | 2);
        Varint(writer, (ulong)bytes.Length);
        writer.Write(bytes);
    }

    internal static void UInt64(IBufferWriter<byte> writer, int field, ulong value)
    {
        Varint(writer, (ulong)field << 3); Varint(writer, value);
    }

    internal static void Varint(IBufferWriter<byte> writer, ulong value)
    {
        var span = writer.GetSpan(10);
        var length = 0;
        do
        {
            span[length++] = (byte)((value & 0x7f) | (value > 0x7f ? 0x80UL : 0));
            value >>= 7;
        } while (value != 0);
        writer.Advance(length);
    }
}
