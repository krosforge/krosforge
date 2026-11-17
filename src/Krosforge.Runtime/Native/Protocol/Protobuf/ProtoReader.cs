namespace Krosforge.Runtime.Native.Protocol.Protobuf;

internal ref struct ProtoReader(ReadOnlySpan<byte> bytes, int maxFields)
{
    private ReadOnlySpan<byte> _remaining = bytes;
    private int _fields;

    internal bool TryRead(out ProtoField field)
    {
        field = default;
        if (_remaining.IsEmpty) return false;
        if (++_fields > maxFields) throw new ProtocolException("Too many protobuf fields.");
        var tag = ReadVarint();
        if (tag > uint.MaxValue || tag >> 3 == 0) throw new ProtocolException("Invalid protobuf field tag.");
        var number = (int)(tag >> 3);
        var kind = (int)(tag & 7);
        if (kind == 0)
        {
            var value = ReadVarint();
            field = new ProtoField(number, kind, default, value);
            return true;
        }
        var length = kind switch
        {
            1 => 8UL,
            2 => ReadVarint(),
            5 => 4UL,
            _ => throw new ProtocolException("Unsupported protobuf wire type.")
        };
        if (length > (ulong)_remaining.Length) throw new ProtocolException("Truncated protobuf field.");
        field = new ProtoField(number, kind, _remaining[..(int)length]);
        _remaining = _remaining[(int)length..];
        return true;
    }

    private ulong ReadVarint()
    {
        ulong value = 0;
        for (var index = 0; index < 10; index++)
        {
            if (_remaining.IsEmpty) throw new ProtocolException("Truncated protobuf varint.");
            var next = _remaining[0];
            _remaining = _remaining[1..];
            if (index == 9 && next > 1) throw new ProtocolException("Protobuf varint overflows uint64.");
            value |= (ulong)(next & 0x7f) << (index * 7);
            if ((next & 0x80) == 0) return value;
        }
        throw new ProtocolException("Unterminated protobuf varint.");
    }
}
