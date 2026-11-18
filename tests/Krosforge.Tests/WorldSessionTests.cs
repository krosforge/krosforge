using System.Net.Sockets;
using System.Text;
using Krosforge.Runtime.Native;
using Krosforge.Runtime.Native.Protocol.Protobuf;
using Xunit;
namespace Krosforge.Tests;
public sealed partial class WorldSessionTests
{
    private static byte[] S(int n, string s) => WorldProtocol.Bytes(n, Encoding.UTF8.GetBytes(s));
    private static async Task<ProtoObject> Login(LocalSessionHost host, TcpClient socket, string ticket, CancellationToken token)
    {
        await socket.ConnectAsync("127.0.0.1", host.ConnectionPort, token);
        return await Connection(socket, 3, WorldProtocol.Bytes(3, S(1, ticket)), token);
    }
    private static async Task<ProtoObject> Connection(TcpClient socket, int operation, byte[] payload, CancellationToken token)
    {
        await WorldProtocol.Write(socket.GetStream(), WorldProtocol.Bytes(1, WorldProtocol.Join(S(1, "1"), WorldProtocol.Bytes(operation, payload))), token);
        var outer = ProtoObject.Read(await LocalSessionHost.ReadFrame(socket.GetStream(), token), 128);
        return ProtoObject.Read(ProtoObject.Read(outer.Bytes(2), 128).Bytes(operation), 128);
    }
    private static async Task Send(TcpClient socket, string type, byte[] payload, CancellationToken token)
    {
        var any = WorldProtocol.Join(S(1, WorldProtocol.Prefix + type), WorldProtocol.Bytes(2, payload));
        await WorldProtocol.Write(socket.GetStream(), WorldProtocol.Bytes(4, WorldProtocol.Bytes(2, any)), token);
    }
    private static async Task<ProtoObject> Event(TcpClient socket, string type, CancellationToken token)
    {
        var outer = ProtoObject.Read(await LocalSessionHost.ReadFrame(socket.GetStream(), token), 128);
        var any = ProtoObject.Read(ProtoObject.Read(outer.Bytes(3), 128).Bytes(1), 128);
        Assert.Equal(WorldProtocol.Prefix + type, any.Text(1));
        return ProtoObject.Read(any.Bytes(2, false), 128);
    }
    private static async Task<TcpClient> Enter(LocalSessionHost host, TcpClient connection, CancellationToken token, bool emptyRoster = true)
    {
        var selected = await Connection(connection, 4, WorldProtocol.Number(1, 290), token);
        var endpoint = ProtoObject.Read(selected.Bytes(1), 128);
        Assert.Equal("127.0.0.1", endpoint.Text(2));
        Assert.Equal(0, await connection.GetStream().ReadAsync(new byte[1], token));
        var world = new TcpClient();
        await world.ConnectAsync("127.0.0.1", host.WorldPort, token);
        await Send(world, "knc", WorldProtocol.Join(S(1, endpoint.Text(1)), S(2, "en")), token);
        await Event(world, "knh", token);
        await Send(world, "ksj", [], token); await Event(world, "ksk", token);
        await Send(world, "ksu", [], token); var roster = await Event(world, "ksv", token); if (emptyRoster) roster.Only();
        return world;
    }
    [Fact]
    public async Task TwoProfilesCanEnterReturnAndReconnectIndependently()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var token = timeout.Token;
        await using var host = new LocalSessionHost();
        using var first = host.Register(new(Guid.NewGuid(), "Alpha", 1), "en", _ => { });
        using var second = host.Register(new(Guid.NewGuid(), "Beta", 2), "pt", _ => { });
        using var a = new TcpClient(); using var b = new TcpClient();
        await Task.WhenAll(Login(host, a, first.IssueTicket(), token), Login(host, b, second.IssueTicket(), token));
        var worlds = await Task.WhenAll(Enter(host, a, token), Enter(host, b, token));
        using var wa = worlds[0]; using var wb = worlds[1];
        await Send(wa, "kru", [], token);
        Assert.Equal(1UL, (await Event(wa, "ksn", token)).UInt64(1));
        await Send(wa, "knd", [], token);
        var returned = await Event(wa, "kmz", token);
        Assert.Equal(1UL, returned.UInt64(1));
        using var again = new TcpClient();
        var account = ProtoObject.Read((await Login(host, again, returned.Text(3), token)).Bytes(1), 128);
        Assert.Equal(1UL, account.UInt64(1));
        using var reentered = await Enter(host, again, token);
        first.Dispose();
        Assert.Equal(0, await reentered.GetStream().ReadAsync(new byte[1], token));
        await Send(wb, "kne", WorldProtocol.Number(2, 1), token);
        Assert.Equal(1UL, (await Event(wb, "knb", token)).UInt64(1));
    }
    [Fact]
    public async Task WrongRealmDoesNotTransferAndUnauthenticatedWorldRequestsClose()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var token = timeout.Token;
        await using var host = new LocalSessionHost();
        using var profile = host.Register(new(Guid.NewGuid(), "Alpha", 1), "en", _ => { });
        using var login = new TcpClient(); await Login(host, login, profile.IssueTicket(), token);
        Assert.True((await Connection(login, 4, WorldProtocol.Number(1, 291), token)).Has(2));
        using var world = await Enter(host, login, token);
        using var invalid = new TcpClient(); await invalid.ConnectAsync("127.0.0.1", host.WorldPort, token);
        await Send(invalid, "ksu", [], token);
        Assert.Equal(0, await invalid.GetStream().ReadAsync(new byte[1], token));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetDuringPartialTransferReleasesSessionForFreshLogin(bool returning)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var token = timeout.Token;
        var partial = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool interrupt = !returning;
        async Task WriteTransfer(NetworkStream stream, byte[] payload, CancellationToken cancellation)
        {
            if (!interrupt) { await WorldProtocol.Write(stream, payload, cancellation); return; }
            // Begin a frame but leave its length incomplete. The peer resets the actual TCP socket.
            await stream.WriteAsync(new byte[] { 0x80 }, cancellation);
            partial.SetResult();
            await Assert.ThrowsAnyAsync<IOException>(async () => { await stream.ReadExactlyAsync(new byte[1], cancellation); });
            throw new IOException("Peer reset during transfer.");
        }
        await using var host = new LocalSessionHost(diagnostic: message =>
        {
            if (message == "connection-ended IOException") failed.TrySetResult();
        }, writeTransfer: WriteTransfer);
        using var profile = host.Register(new(Guid.NewGuid(), "Interrupted", 1), "en", _ => { });
        using var other = host.Register(new(Guid.NewGuid(), "Unaffected", 2), "pt", _ => { });
        // Establish the second account before enabling the fault.
        interrupt = false;
        using var otherLogin = new TcpClient(); await Login(host, otherLogin, other.IssueTicket(), token);
        using var otherWorld = await Enter(host, otherLogin, token);
        using var login = new TcpClient(); await Login(host, login, profile.IssueTicket(), token);
        using var world = returning ? await Enter(host, login, token) : null;
        interrupt = true;
        var victim = world ?? login;
        if (returning) await Send(victim, "knd", [], token);
        else await WorldProtocol.Write(victim.GetStream(), WorldProtocol.Bytes(1,
            WorldProtocol.Join(S(1, "2"), WorldProtocol.Bytes(4, WorldProtocol.Number(1, 290)))), token);
        await partial.Task.WaitAsync(token);
        var prefix = new byte[1]; await victim.GetStream().ReadExactlyAsync(prefix, token);
        Assert.Equal(0x80, prefix[0]);
        victim.Client.LingerState = new LingerOption(true, 0); victim.Dispose();
        await failed.Task.WaitAsync(token);
        interrupt = false;
        using var fresh = new TcpClient();
        var account = ProtoObject.Read((await Login(host, fresh, profile.IssueTicket(), token)).Bytes(1), 128);
        Assert.Equal(1UL, account.UInt64(1));
        using var recovered = await Enter(host, fresh, token);
        await Send(otherWorld, "kne", WorldProtocol.Number(2, 1), token);
        Assert.Equal(1UL, (await Event(otherWorld, "knb", token)).UInt64(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateTransferWriteFailureCannotReleaseAnAlreadyAttachedSocket(bool returning)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var token = timeout.Token;
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool interrupt = !returning;
        async Task WriteTransfer(NetworkStream stream, byte[] payload, CancellationToken cancellation)
        {
            await WorldProtocol.Write(stream, payload, cancellation);
            if (!interrupt) return;
            await attached.Task.WaitAsync(cancellation);
            throw new IOException("Late transport failure after delivery.");
        }
        await using var host = new LocalSessionHost(diagnostic: message =>
        { if (message == "connection-ended IOException") cleaned.TrySetResult(); }, writeTransfer: WriteTransfer);
        using var profile = host.Register(new(Guid.NewGuid(), "Replacement", 1), "en", _ => { });
        using var login = new TcpClient(); await Login(host, login, profile.IssueTicket(), token);
        using var oldWorld = returning ? await Enter(host, login, token) : null;
        interrupt = true;
        using var replacement = new TcpClient();
        if (returning)
        {
            await Send(oldWorld!, "knd", [], token);
            var ticket = (await Event(oldWorld!, "kmz", token)).Text(3);
            await Login(host, replacement, ticket, token);
        }
        else
        {
            var endpoint = ProtoObject.Read((await Connection(login, 4, WorldProtocol.Number(1, 290), token)).Bytes(1), 128);
            await replacement.ConnectAsync("127.0.0.1", host.WorldPort, token);
            await Send(replacement, "knc", S(1, endpoint.Text(1)), token);
            await Event(replacement, "knh", token);
        }
        attached.SetResult(); await cleaned.Task.WaitAsync(token); interrupt = false;
        if (returning) { using var recovered = await Enter(host, replacement, token); }
        else
        {
            await Send(replacement, "ksj", [], token); await Event(replacement, "ksk", token);
            await Send(replacement, "ksu", [], token); (await Event(replacement, "ksv", token)).Only();
        }
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbandonedDeliveredTicketExpiresAndAllowsFreshTcpAuthentication(bool returning)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var token = timeout.Token;
        var clock = new Clock();
        await using var host = new LocalSessionHost(clock: clock);
        using var profile = host.Register(new(Guid.NewGuid(), "Abandoned", 1), "en", _ => { });
        using var login = new TcpClient(); await Login(host, login, profile.IssueTicket(), token);
        using var world = returning ? await Enter(host, login, token) : null;
        if (returning) { await Send(world!, "knd", [], token); await Event(world!, "kmz", token); }
        else await Connection(login, 4, WorldProtocol.Number(1, 290), token);
        var abandoned = world ?? login;
        Assert.Equal(0, await abandoned.GetStream().ReadAsync(new byte[1], token));
        // The response was delivered but the peer never uses its transfer credential.
        abandoned.Dispose(); clock.Now += TimeSpan.FromSeconds(31);
        using var fresh = new TcpClient(); await Login(host, fresh, profile.IssueTicket(), token);
        using var recovered = await Enter(host, fresh, token);
    }

}
