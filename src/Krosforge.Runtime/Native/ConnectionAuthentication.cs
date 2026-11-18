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
    internal static byte[] Accepted(Profile profile, IReadOnlyList<CharacterWire>? characters = null)
    {
        var collection = new ArrayBufferWriter<byte>();
        foreach (var id in new[] { ServerPresentation.RealmId })
        {
            var descriptor = new ArrayBufferWriter<byte>();
            ProtoWriter.UInt64(descriptor, 1, (ulong)id);
            ProtoWriter.UInt64(descriptor, 2, 0);
            ProtoWriter.UInt64(descriptor, 3, 1);
            var realm = new ArrayBufferWriter<byte>();
            ProtoWriter.Bytes(realm, 1, descriptor.WrittenSpan);
            // mgg field 2 is the realm status (0 = online), not a character count.
            ProtoWriter.UInt64(realm, 2, 0);
            foreach (var character in characters ?? [])
            {
                var summary = new ArrayBufferWriter<byte>();
                ProtoWriter.Bytes(summary, 1, Encoding.UTF8.GetBytes(character.Name));
                // mgt.mgs.mgr is a connection enum, distinct from the catalog ID.
                var breed = character.BreedId switch
                {
                    >= 1 and <= 18 => character.BreedId - 1,
                    20 => 18,
                    _ => throw new ProtocolException("Unsupported summary breed.")
                };
                ProtoWriter.UInt64(summary, 2, (ulong)breed);
                ProtoWriter.UInt64(summary, 3, (ulong)character.Gender);
                ProtoWriter.UInt64(summary, 4, (ulong)character.Level);
                // No world-access history exists yet. The consumer parses an ISO date.
                ProtoWriter.Bytes(summary, 5, "1970-01-01T00:00:00Z"u8);
                ProtoWriter.Bytes(realm, 3, summary.WrittenSpan);
            }
            ProtoWriter.Bytes(collection, 1, realm.WrittenSpan);
        }
        var quota = new ArrayBufferWriter<byte>();
        ProtoWriter.UInt64(quota, 1, 1);
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
