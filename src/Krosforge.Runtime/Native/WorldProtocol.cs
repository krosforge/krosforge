using System.Buffers;
using System.Text;
using Krosforge.Runtime.Native.Protocol;
using Krosforge.Runtime.Native.Protocol.Protobuf;
namespace Krosforge.Runtime.Native;

// Build-specific interoperability identifiers. No game content is embedded here.
internal static class WorldProtocol
{
    internal const string Prefix = "type.ankama.com/";
    internal static (string Type, byte[] Payload) Read(byte[] bytes)
    {
        var outer = ProtoObject.Read(bytes, 128); outer.Only(4);
        var request = ProtoObject.Read(outer.Bytes(4), 128); request.Only(1, 2);
        var uid = unchecked((long)request.UInt64(1, false));
        if (uid is < int.MinValue or > int.MaxValue) throw new ProtocolException("Invalid request ID.");
        var any = ProtoObject.Read(request.Bytes(2), 128); any.Only(1, 2);
        var type = any.Text(1);
        if (!type.StartsWith(Prefix, StringComparison.Ordinal) || type.Length > 160) throw new ProtocolException("Invalid message type.");
        var name = type[Prefix.Length..];
        if (name.Length == 0 || (!char.IsAsciiLetter(name[0]) && name[0] != '_') || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or '+'))) throw new ProtocolException("Invalid message name.");
        return (name, any.Bytes(2, false));
    }
    internal static byte[] Event(string type, byte[] payload) => Bytes(3, Bytes(1, Join(Bytes(1, Encoding.UTF8.GetBytes(Prefix + type)), Bytes(2, payload))));
    internal static byte[] Number(int field, ulong value) { var w = new ArrayBufferWriter<byte>(); ProtoWriter.UInt64(w, field, value); return w.WrittenSpan.ToArray(); }
    internal static byte[] Bytes(int field, byte[] bytes) { var w = new ArrayBufferWriter<byte>(); ProtoWriter.Bytes(w, field, bytes); return w.WrittenSpan.ToArray(); }
    internal static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    internal static byte[] Selected(string ticket, int port)
    {
        var ports = new ArrayBufferWriter<byte>(); ProtoWriter.Varint(ports, (ulong)port);
        return Bytes(1, Join(Bytes(1, Encoding.UTF8.GetBytes(ticket)), Bytes(2, Encoding.UTF8.GetBytes("127.0.0.1")), Bytes(3, ports.WrittenSpan.ToArray())));
    }
    internal static async Task Write(Stream stream, byte[] payload, CancellationToken token)
    {
        var w = new ArrayBufferWriter<byte>(); ProtoWriter.Varint(w, (ulong)payload.Length); w.Write(payload);
        await stream.WriteAsync(w.WrittenMemory, token);
    }
}
