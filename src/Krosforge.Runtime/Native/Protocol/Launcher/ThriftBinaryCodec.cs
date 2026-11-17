using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Krosforge.Runtime.Native.Protocol.Launcher;

/// <summary>Strict, unframed TBinaryProtocol for the launcher values recovered from this build.</summary>
public sealed class ThriftBinaryCodec
{
    private const uint Version = 0x80010000;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly ThriftLimits _limits;
    public ThriftBinaryCodec(ThriftLimits limits) { limits.Validate(); _limits = limits; }

    public async Task<ThriftMessage> ReadAsync(Stream stream, CancellationToken token, byte? firstHeaderByte = null)
    {
        var input = new Input(stream, _limits, token, firstHeaderByte);
        var header = unchecked((uint)await input.Int32Async());
        if ((header & 0xffffff00) != Version || (header & 255) is < 1 or > 3)
            throw new ProtocolException("Expected strict Thrift header.");
        var method = await input.StringAsync(128);
        ValidateMethod(method);
        var sequence = await input.Int32Async();
        return new ThriftMessage(method, (ThriftMessageKind)(header & 255), sequence, await input.FieldsAsync());
    }

    public byte[] Encode(ThriftMessage message)
    {
        ValidateMethod(message.Method);
        if (message.Kind is < ThriftMessageKind.Call or > ThriftMessageKind.Exception || message.Fields.Count > _limits.MaxFields)
            throw new ProtocolException("Invalid launcher message.");
        var output = new ArrayBufferWriter<byte>();
        WriteInt32(output, unchecked((int)(Version | (uint)message.Kind)));
        WriteString(output, message.Method, 128);
        WriteInt32(output, message.Sequence);
        var remainingFields = _limits.MaxFields;
        WriteFields(output, message.Fields, ref remainingFields);
        return output.WrittenSpan.ToArray();
    }

    private void WriteFields(ArrayBufferWriter<byte> output, IReadOnlyDictionary<short, ThriftValue> fields, ref int remainingFields)
    {
        foreach (var (id, value) in fields.OrderBy(pair => pair.Key))
        {
            if (remainingFields-- <= 0) throw new ProtocolException("Too many launcher fields.");
            output.Write([(byte)value.Kind]);
            var field = output.GetSpan(2); BinaryPrimitives.WriteInt16BigEndian(field, id); output.Advance(2);
            switch (value.Kind)
            {
                case ThriftValueKind.String when value.Text is not null:
                    WriteString(output, value.Text, _limits.MaxStringBytes); break;
                case ThriftValueKind.Int32: WriteInt32(output, value.Number); break;
                case ThriftValueKind.Bool when value.Number is 0 or 1: output.Write([(byte)value.Number]); break;
                case ThriftValueKind.Struct when value.Members is not null:
                    WriteFields(output, value.Members, ref remainingFields); break;
                case ThriftValueKind.List when value.Items is not null && value.Items.Count <= _limits.MaxFields:
                    output.Write([(byte)ThriftValueKind.String]);
                    WriteInt32(output, value.Items.Count);
                    foreach (var item in value.Items)
                    {
                        WriteString(output, item, _limits.MaxStringBytes);
                        CheckOutputBudget(output);
                    }
                    break;
                default: throw new ProtocolException("Invalid launcher result type.");
            }
            CheckOutputBudget(output);
        }
        output.Write([(byte)0]);
        CheckOutputBudget(output);
    }

    private void CheckOutputBudget(ArrayBufferWriter<byte> output)
    {
        if (output.WrittenCount > _limits.MaxMessageBytes) throw new ProtocolException("Launcher output exceeds byte budget.");
    }

    private static void ValidateMethod(string method)
    {
        if (method.Length is 0 or > 128 || method.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ProtocolException("Invalid launcher method.");
    }

    private static void WriteInt32(IBufferWriter<byte> output, int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(output.GetSpan(4), value); output.Advance(4);
    }
    private void WriteString(ArrayBufferWriter<byte> output, string value, int limit)
    {
        var count = Utf8.GetByteCount(value);
        if (count > limit) throw new ProtocolException("Launcher string exceeds budget.");
        if (count > _limits.MaxMessageBytes - output.WrittenCount - sizeof(int))
            throw new ProtocolException("Launcher output exceeds byte budget.");
        WriteInt32(output, count);
        Utf8.GetBytes(value, output.GetSpan(count)); output.Advance(count);
    }

    private sealed class Input(Stream stream, ThriftLimits limits, CancellationToken token, byte? prefix)
    {
        private int _remaining = limits.MaxMessageBytes;
        private int _remainingFields = limits.MaxFields;

        internal async Task<IReadOnlyDictionary<short, ThriftValue>> FieldsAsync()
        {
            var fields = new Dictionary<short, ThriftValue>();
            while (true)
            {
                var kind = (ThriftValueKind)await ByteAsync();
                if (kind == 0) return fields;
                // Shared across nested structs: nesting cannot reset the allocation budget.
                if (_remainingFields-- <= 0) throw new ProtocolException("Too many launcher fields.");
                var id = await Int16Async();
                var value = kind switch
                {
                    ThriftValueKind.String => ThriftValue.String(await StringAsync(limits.MaxStringBytes)),
                    ThriftValueKind.Int32 => ThriftValue.Int32(await Int32Async()),
                    ThriftValueKind.Bool => (await ByteAsync()) switch
                    {
                        0 => ThriftValue.Bool(false), 1 => ThriftValue.Bool(true),
                        _ => throw new ProtocolException("Invalid Thrift boolean.")
                    },
                    ThriftValueKind.Struct => ThriftValue.Struct(await FieldsAsync()),
                    ThriftValueKind.List => ThriftValue.StringList(await StringListAsync()),
                    _ => throw new ProtocolException("Unsupported launcher argument type.")
                };
                if (!fields.TryAdd(id, value)) throw new ProtocolException("Duplicate launcher field.");
            }
        }

        private async Task<IReadOnlyList<string>> StringListAsync()
        {
            if ((ThriftValueKind)await ByteAsync() != ThriftValueKind.String)
                throw new ProtocolException("Unsupported launcher list element type.");
            var count = await Int32Async();
            if (count < 0 || count > limits.MaxFields) throw new ProtocolException("Invalid launcher list length.");
            var items = new string[count];
            for (var index = 0; index < count; index++) items[index] = await StringAsync(limits.MaxStringBytes);
            return items;
        }

        private async Task<byte[]> TakeAsync(int count)
        {
            if (count < 0 || count > _remaining) throw new ProtocolException("Launcher message exceeds budget.");
            _remaining -= count;
            var bytes = new byte[count];
            var offset = 0;
            if (count > 0 && prefix is not null) { bytes[0] = prefix.Value; prefix = null; offset = 1; }
            await stream.ReadExactlyAsync(bytes.AsMemory(offset), token); return bytes;
        }
        internal async Task<byte> ByteAsync() => (await TakeAsync(1))[0];
        internal async Task<short> Int16Async() => BinaryPrimitives.ReadInt16BigEndian(await TakeAsync(2));
        internal async Task<int> Int32Async() => BinaryPrimitives.ReadInt32BigEndian(await TakeAsync(4));
        internal async Task<string> StringAsync(int limit)
        {
            var count = await Int32Async();
            if (count < 0 || count > limit) throw new ProtocolException("Invalid launcher string length.");
            try { return Utf8.GetString(await TakeAsync(count)); }
            catch (DecoderFallbackException) { throw new ProtocolException("Invalid launcher UTF-8 string."); }
        }
    }
}
