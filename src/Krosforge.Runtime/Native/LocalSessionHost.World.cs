using System.Net.Sockets;
using System.Text;
using Krosforge.Runtime.Native.Protocol;
using Krosforge.Runtime.Native.Protocol.Protobuf;
namespace Krosforge.Runtime.Native;

internal sealed partial class LocalSessionHost
{
    private async Task ServeWorld(NetworkStream stream, CancellationToken token)
    {
        Session? session = null;
        long lease = 0;
        bool transferred = false, ready = false;
        int unsupported = 0;
        using var authentication = CancellationTokenSource.CreateLinkedTokenSource(token);
        authentication.CancelAfter(authenticationTimeout);
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session is null ? authentication.Token : token, session?.Token ?? token);
                var request = WorldProtocol.Read(await ReadFrame(stream, deadline.Token));
                diagnostic?.Invoke("world-request " + request.Type);
                var body = ProtoObject.Read(request.Payload, 128);
                byte[] reply;
                if (request.Type == "knc")
                {
                    if (session is not null) throw new ProtocolException("Already authenticated.");
                    body.Only(1, 2);
                    var ticket = body.Text(1);
                    if (ticket.Length != 64) throw new ProtocolException("Invalid ticket.");
                    body.Text(2, false);
                    foreach (var candidate in sessions.Values)
                    {
                        lease = candidate.Transfer.Attach(ticket, returning: false);
                        if (lease != 0) { session = candidate; break; }
                    }
                    if (session is null) throw new ProtocolException("World ticket rejected.");
                    session.Token.ThrowIfCancellationRequested();
                    reply = WorldProtocol.Event("knh", []);
                }
                else
                {
                    if (session is null || !session.Transfer.Current(lease, "world")) throw new ProtocolException("World authentication required.");
                    switch (request.Type)
                    {
                        case "ksj": body.Only(); ready = true; reply = WorldProtocol.Event("ksk", []); break;
                        case "kne":
                            body.Only(2);
                            var silent = body.UInt64(2, false);
                            if (silent > 1) throw new ProtocolException("Invalid ping.");
                            reply = WorldProtocol.Event("knb", WorldProtocol.Number(1, silent)); break;
                        case "knd":
                            body.Only();
                            var returned = session.Transfer.Return(lease) ?? throw new ProtocolException("Return rejected.");
                            lease = returned.Lease;
                            session.Phase("returning-server");
                            await WorldProtocol.Write(stream, WorldProtocol.Event("kmz", WorldProtocol.Join(WorldProtocol.Number(1, 1), WorldProtocol.Bytes(3, Encoding.UTF8.GetBytes(returned.Ticket)))), deadline.Token);
                            transferred = true;
                            return;
                        case "ksu" when ready:
                            body.Only(); reply = WorldProtocol.Event("ksv", []); break;
                        case "kru" when ready: reply = WorldProtocol.Event("ksn", WorldProtocol.Number(1, 1)); break;
                        case "kta" when ready: reply = WorldProtocol.Event("ksb", WorldProtocol.Bytes(2, [])); break;
                        case "ksm" or "kso" when ready: reply = WorldProtocol.Event("kst", WorldProtocol.Number(1, 0)); break;
                        case "kkc": throw new ProtocolException("No character selected.");
                        default:
                            if (!ready || ++unsupported > 128) throw new ProtocolException("Unsupported request budget exceeded.");
                            // Unknown optional requests have no fabricated reply or state change.
                            continue;
                    }
                }
                await WorldProtocol.Write(stream, reply, deadline.Token);
                if (request.Type == "ksu") session!.Phase("character-selection");
            }
        }
        finally
        {
            if (session is not null && !transferred)
            {
                session.ReleaseNetwork(lease);
            }
        }
    }
}
