using System.Text;

namespace Krosforge.Runtime.Native.Protocol.Protobuf;

internal sealed record ProtoValue(int WireType, ulong Number, byte[] Bytes);

/// <summary>Owned scalar fields with strict singular access; no spans escape the input buffer.</summary>
internal sealed class ProtoObject
{
    private readonly Dictionary<int, List<ProtoValue>> _fields = [];
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static ProtoObject Read(ReadOnlySpan<byte> bytes, int maxFields)
    {
        var result = new ProtoObject(); var reader = new ProtoReader(bytes, maxFields);
        while (reader.TryRead(out var field))
        {
            if (!result._fields.TryGetValue(field.Number, out var values)) result._fields[field.Number] = values = [];
            values.Add(new ProtoValue(field.WireType, field.UInt64, field.Bytes.ToArray()));
        }
        return result;
    }
    internal bool Has(int field) => _fields.ContainsKey(field);
    internal IEnumerable<ProtoValue> Repeated(int field) => _fields.GetValueOrDefault(field) ?? [];
    internal byte[] Bytes(int field, bool required = true) => Singular(field, 2, required)?.Bytes ?? [];
    internal ulong UInt64(int field, bool required = true) => Singular(field, 0, required)?.Number ?? 0;
    internal string Text(int field, bool required = true)
    {
        try { return Utf8.GetString(Bytes(field, required)); }
        catch (DecoderFallbackException) { throw new ProtocolException("Invalid UTF-8 field."); }
    }
    internal void Only(params int[] fields)
    {
        if (_fields.Keys.Any(field => !fields.Contains(field))) throw new ProtocolException("Unsupported field in strict contract.");
    }
    private ProtoValue? Singular(int field, int wireType, bool required)
    {
        if (!_fields.TryGetValue(field, out var values))
            return required ? throw new ProtocolException("Required protobuf field is absent.") : null;
        if (values.Count != 1 || values[0].WireType != wireType) throw new ProtocolException("Duplicate or mistyped singular field.");
        return values[0];
    }
}
