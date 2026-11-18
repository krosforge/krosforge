using System.Buffers;
using System.Text;
using Krosforge.Contracts;
using Krosforge.Runtime.Native.Protocol;
using Krosforge.Runtime.Native.Protocol.Protobuf;
namespace Krosforge.Runtime.Native;

// Technical interoperability mapping, validated against the hash-pinned native client.
internal static class ConnectionAuthentication
{
    internal const int Authenticate = 3;
    internal const int SelectRealm = 4;
    internal static string ReadTicket(ReadOnlySpan<byte> payload)
    {
        var request = ProtoObject.Read(payload, 128);
        if (request.Has(4)) throw new ProtocolException("Only local tickets are accepted.");
        var credential = ProtoObject.Read(request.Bytes(3), 128);
        var selector = ProtoObject.Read(credential.Bytes(2, false), 128);
        selector.Only(1, 2);
        if (selector.UInt64(1, false) != 0 || selector.Text(2, false) != "") throw new ProtocolException("Account selector rejected.");
        var ticket = credential.Text(1);
        if (ticket.Length is < 1 or > 4096) throw new ProtocolException("Ticket extent rejected.");
        return ticket;
    }
    internal static byte[] Accepted(Profile profile)
    {
        var collection = new ArrayBufferWriter<byte>();
        foreach (var id in new[] { ServerPresentation.RealmId })
        {
            var descriptor = new ArrayBufferWriter<byte>();
            ProtoWriter.UInt64(descriptor, 1, (ulong)id);
            ProtoWriter.UInt64(descriptor, 2, 0);
            ProtoWriter.UInt64(descriptor, 3, 0);
            var realm = new ArrayBufferWriter<byte>();
            ProtoWriter.Bytes(realm, 1, descriptor.WrittenSpan);
            ProtoWriter.UInt64(realm, 2, 0);
            ProtoWriter.Bytes(collection, 1, realm.WrittenSpan);
        }
        var quota = new ArrayBufferWriter<byte>();
        ProtoWriter.UInt64(quota, 1, 0);
        ProtoWriter.UInt64(quota, 2, 5);
        ProtoWriter.Bytes(collection, 2, quota.WrittenSpan);
        var account = new ArrayBufferWriter<byte>();
        ProtoWriter.UInt64(account, 1, (ulong)profile.Slot);
        ProtoWriter.Bytes(account, 2, Encoding.UTF8.GetBytes(profile.Name));
        ProtoWriter.Bytes(account, 3, Encoding.UTF8.GetBytes(profile.Slot.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)));
        ProtoWriter.Bytes(account, 4, collection.WrittenSpan);
        ProtoWriter.Bytes(account, 6, []);
        var result = new ArrayBufferWriter<byte>();
        ProtoWriter.Bytes(result, 1, account.WrittenSpan);
        return result.WrittenSpan.ToArray();
    }
    internal static byte[] RealmUnavailable()
    {
        var response = new ArrayBufferWriter<byte>();
        ProtoWriter.UInt64(response, 2, 0);
        return response.WrittenSpan.ToArray();
    }
}
