using System.Buffers;
using System.Globalization;
using System.Text;
using Krosforge.Runtime.Native.Protocol.Protobuf;

namespace Krosforge.Runtime.Native.Protocol.Connection;

/// <summary>Decodes request envelopes and the verified protocol-negotiation reply.</summary>
public sealed class ConnectionEnvelopeCodec
{
    private const int MaxCorrelationBytes = 11; // Decimal representation of a signed int32.
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly ConnectionProfile _profile;
    private readonly int _maxFields;

    public ConnectionEnvelopeCodec(ConnectionProfile profile, int maxFields)
    {
        profile.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFields);
        _profile = profile;
        _maxFields = maxFields;
    }

    public ConnectionRequest DecodeRequest(ReadOnlySpan<byte> bytes)
    {
        var outer = new ProtoReader(bytes, _maxFields);
        byte[]? innerBytes = null;
        var found = false;
        while (outer.TryRead(out var field))
        {
            if (field.Number != _profile.RequestEnvelopeField || field.WireType != 2 || found)
                throw new ProtocolException("Expected exactly one request envelope.");
            found = true;
            innerBytes = field.Bytes.ToArray();
        }
        if (!found) throw new ProtocolException("Missing request envelope.");
        var reader = new ProtoReader(innerBytes!, _maxFields);
        string? uid = null;
        ReadOnlyMemory<byte> payload = default;
        int? operation = null;
        while (reader.TryRead(out var field))
        {
            if (field.Number == _profile.CorrelationIdField)
            {
                if (field.WireType != 2 || uid is not null) throw new ProtocolException("Invalid correlation field.");
                if (field.Bytes.Length > MaxCorrelationBytes) throw new ProtocolException("Correlation ID exceeds int32 text length.");
                try { uid = Utf8.GetString(field.Bytes); }
                catch (DecoderFallbackException) { throw new ProtocolException("Invalid UTF-8 correlation ID."); }
            }
            else
            {
                if (field.WireType != 2 || operation is not null) throw new ProtocolException("Invalid request variant.");
                operation = field.Number;
                payload = field.Bytes.ToArray();
            }
        }
        ValidateCorrelation(uid);
        if (operation is null) throw new ProtocolException("Missing request variant.");
        return new ConnectionRequest(uid!, operation.Value, payload);
    }

    public byte[] ReplyToProtocolNegotiation(ConnectionRequest request)
    {
        if (request.Operation != _profile.ProtocolNegotiationOperation)
            throw new ProtocolException("Connection operation is not implemented in this phase.");
        ValidateCorrelation(request.CorrelationId);
        var payload = new ProtoReader(request.Payload.Span, _maxFields);
        while (payload.TryRead(out _)) { }
        return EncodeResponse(request.CorrelationId, request.Operation, []);
    }

    public byte[] EncodeResponse(string correlationId, int operation, ReadOnlySpan<byte> payload)
    {
        ValidateCorrelation(correlationId);
        if (operation is <= 0 or >= (1 << 29)) throw new ProtocolException("Invalid response operation.");
        var inner = new ArrayBufferWriter<byte>();
        ProtoWriter.Bytes(inner, _profile.CorrelationIdField, Utf8.GetBytes(correlationId));
        ProtoWriter.Bytes(inner, operation, payload);
        var outer = new ArrayBufferWriter<byte>();
        ProtoWriter.Bytes(outer, _profile.ResponseEnvelopeField, inner.WrittenSpan);
        return outer.WrittenSpan.ToArray();
    }

    private static void ValidateCorrelation(string? uid)
    {
        if (string.IsNullOrEmpty(uid) || !int.TryParse(uid, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            throw new ProtocolException("Correlation ID must be a decimal int32 string.");
        var start = uid[0] == '-' ? 1 : 0;
        for (var index = start; index < uid.Length; index++)
            if (uid[index] is < '0' or > '9') throw new ProtocolException("Invalid correlation character.");
    }
}
