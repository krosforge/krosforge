using System.Net.Sockets;
using System.Text;
using Krosforge.Runtime.Native.Protocol;
using Krosforge.Runtime.Native.Protocol.Protobuf;
namespace Krosforge.Runtime.Native;

internal sealed partial class LocalSessionHost
{
    private async Task ServeWorld(NetworkStream stream, CancellationToken token)
    {
        await using var output = new WorldOutput(stream, token);
        Session? session = null;
        long lease = 0;
        bool transferred = false, ready = false, returnIssued = false;
        int unsupported = 0;
        long? pendingDeletion = null;
        var connectionIdentity = new object();
        WorldEntryService.Selection? selected = null;
        var characterWire = new CharacterWireCodec();
        using var authentication = CancellationTokenSource.CreateLinkedTokenSource(token);
        authentication.CancelAfter(authenticationTimeout);
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session is null ? authentication.Token : output.Token, session?.Token ?? output.Token);
                var request = WorldProtocol.Read(await ReadFrame(stream, deadline.Token));
                diagnostic?.Invoke("world-request " + request.Type);
                var body = ProtoObject.Read(request.Payload, request.Type == "jok" ? 600 : 128);
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
                            selected?.Dispose(); selected = null;
                            var returned = session.Transfer.Return(lease) ?? throw new ProtocolException("Return rejected.");
                            lease = returned.Lease;
                            returnIssued = true;
                            session.Phase("returning-server");
                            await output.Send(null, deadline.Token);
                            await writeTransfer(stream, WorldProtocol.Event("kmz", WorldProtocol.Join(WorldProtocol.Number(1, 1), WorldProtocol.Bytes(3, Encoding.UTF8.GetBytes(returned.Ticket)))), deadline.Token);
                            transferred = true;
                            return;
                        case "ksu" when ready && selected is null:
                            body.Only(); pendingDeletion = null; reply = WorldProtocol.Event("ksv", characters is null ? [] : characterWire.List(characters.List(session.Profile.Id))); break;
                        case "kru" when ready && selected is null:
                            pendingDeletion = null;
                            if (characters is null) { reply = WorldProtocol.Event("ksn", characterWire.CreationResult("failure")); break; }
                            var result = characters.Create(session.Profile.Id, characterWire.ReadCreation(request.Payload), out var createdId);
                            await output.Send(WorldProtocol.Event("ksn", characterWire.CreationResult(result)), deadline.Token);
                            if (result != "success") continue;
                            // The native creation flow selects the first entry in this response.
                            reply = WorldProtocol.Event("ksv", characterWire.List(characters.List(session.Profile.Id).OrderByDescending(c => c.Id == createdId))); break;
                        case "ktb" when ready && characters is not null:
                            body.Only(); reply = WorldProtocol.Event("ksp", characterWire.SuggestedName(characters.Suggest())); break;
                        case "kta" when ready:
                            pendingDeletion = null;
                            var selectedId = characterWire.SelectionId(request.Payload);
                            if (selected is null) selected = worldEntry?.Select(session.Profile.Id, session.Profile.Slot, connectionIdentity, selectedId, notice =>
                            {
                                if (notice is ActorCorrected correction)
                                    diagnostic?.Invoke($"world-correction actor={correction.Id} cell={correction.Cell}");
                                foreach (var frame in WorldEntryWire.Events(notice)) output.Post(frame);
                            }, output.Abort);
                            if (selected is not null && selected.Character.Id != selectedId) throw new ProtocolException("Character already selected.");
                            reply = WorldProtocol.Event("ksb", selected is null ? characterWire.SelectionRejected() : characterWire.SelectionAccepted(selected.Character));
                            break;
                        case "ksm" when ready && selected is null:
                            pendingDeletion = null;
                            var deleteId = characterWire.DeletionPreparationId(request.Payload);
                            if (characters is not null && characters.List(session.Profile.Id).Any(c => c.Id == deleteId))
                            { pendingDeletion = deleteId; reply = WorldProtocol.Event("ksw", characterWire.DeletionPrepared()); }
                            else reply = WorldProtocol.Event("kst", characterWire.DeletionRejected());
                            break;
                        case "kso" when ready && selected is null:
                            var confirmed = characterWire.DeletionConfirmationId(request.Payload);
                            var pending = pendingDeletion; pendingDeletion = null;
                            reply = characters is not null && pending == confirmed && characters.Delete(session.Profile.Id, confirmed)
                                ? WorldProtocol.Event("ksv", characterWire.List(characters.List(session.Profile.Id)))
                                : WorldProtocol.Event("kst", characterWire.DeletionRejected()); break;
                        case "kkc" when selected is not null:
                            WorldEntryWire.ReadContext(request.Payload);
                            if (selected.ContextCreated) throw new ProtocolException("Context already created.");
                            selected.ContextCreated = true;
                            await output.Send(WorldProtocol.Event("kjp", WorldEntryWire.Context()), deadline.Token);
                            await output.Send(WorldProtocol.Event("jow", WorldEntryWire.LoadMap(selected.Location.MapId)), deadline.Token);
                            await output.Send(WorldProtocol.Event("irl", WorldEntryWire.EmptyInventory()), deadline.Token);
                            reply = WorldProtocol.Event("krg", WorldEntryWire.Statistics(selected));
                            break;
                        case "kjt" when selected is { ContextCreated: true }:
                            // Native optional requests recur after each scene load. Keep
                            // the unknown-request budget per admitted map, not per lifetime.
                            if (!selected.MapReady) unsupported = 0;
                            worldEntry!.Join(selected, WorldEntryWire.ReadMap(request.Payload));
                            await output.Send(null, deadline.Token);
                            session.Phase("in-world");
                            continue;
                        case "jok" when selected is { MapReady: true }:
                            var movement = WorldMovementWire.Read(request.Payload);
                            diagnostic?.Invoke($"world-path map={movement.Map} cells={string.Join(',', movement.TurningCells)}");
                            worldEntry!.Move(selected, movement);
                            continue;
                        case "jny" when selected is { MapReady: true }:
                            // This protocol ACK has no movement identity. It never advances
                            // the server clock or commits a position, including when delayed.
                            WorldMovementWire.Acknowledgement(request.Payload);
                            worldEntry!.Acknowledge(selected);
                            continue;
                        case "jpj" when selected is { MapReady: true }:
                            var cancellation = WorldMovementWire.Cancellation(request.Payload);
                            diagnostic?.Invoke($"world-cancel cell={cancellation}");
                            worldEntry!.Cancel(selected, cancellation);
                            continue;
                        case "joc" when selected is { MapReady: true }:
                            worldEntry!.ChangeMap(selected, WorldMovementWire.ChangeMap(request.Payload));
                            continue;
                        case "kkc": throw new ProtocolException("No character selected.");
                        default:
                            if (!ready || ++unsupported > 128) throw new ProtocolException("Unsupported request budget exceeded.");
                            // Unknown optional requests have no fabricated reply or state change.
                            continue;
                    }
                }
                await output.Send(reply, deadline.Token);
                if (request.Type == "ksu") session!.Phase("character-selection");
                if (request.Type == "kta" && selected is not null) session!.Phase("entering-world");
            }
        }
        finally
        {
            selected?.Dispose();
            if (session is not null && !transferred)
            {
                session.ReleaseNetwork(lease, returnIssued ? "return" : "world");
            }
        }
    }
}
