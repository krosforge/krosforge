namespace Krosforge.Runtime.Native.Protocol.Protobuf;

internal readonly ref struct ProtoField(int number, int wireType, ReadOnlySpan<byte> bytes, ulong uint64 = 0)
{
    internal ulong UInt64 { get; } = uint64;
    internal int Number { get; } = number;
    internal int WireType { get; } = wireType;
    internal ReadOnlySpan<byte> Bytes { get; } = bytes;
}
