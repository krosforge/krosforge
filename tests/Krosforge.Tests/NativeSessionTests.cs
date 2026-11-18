using System.Buffers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Krosforge.Contracts;
using Krosforge.Runtime.Native;
using Krosforge.Runtime.Native.Protocol.Connection;
using Krosforge.Runtime.Native.Protocol.Launcher;
using Krosforge.Runtime.Native.Protocol.Protobuf;
using Xunit;
namespace Krosforge.Tests;

public sealed class NativeSessionTests
{
    [Fact]
    public async Task LocalLauncherTicketAuthenticatesAndReturnsServerList()
    {
        var profile = new Profile(Guid.NewGuid(), "Local test", 7);
        var phases = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var host = new LocalSessionHost();
        using var registration = host.Register(profile, "en", phases.Enqueue);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var http = new HttpClient();
        var config = JsonDocument.Parse(await http.GetStringAsync(host.BaseUrl + "/config.json", timeout.Token));
        Assert.Equal($"local:127.0.0.1:{host.ConnectionPort}", config.RootElement.GetProperty("connectionHosts")[0].GetString());
        Assert.StartsWith(host.BaseUrl, config.RootElement.GetProperty("haapiAnkamaUrl").GetString());
        using var launcher = new TcpClient();
        await launcher.ConnectAsync("127.0.0.1", host.LauncherPort, timeout.Token);
        var codec = new ThriftBinaryCodec(new(65536, 8192, 128));
        var session = await Rpc(codec, launcher.GetStream(), "connect", new()
        { [1] = ThriftValue.String("dofus"), [2] = ThriftValue.String("main"), [3] = ThriftValue.Int32(7), [4] = ThriftValue.String(registration.ConnectSecret) }, timeout.Token);
        var ticket = await Rpc(codec, launcher.GetStream(), "auth_getGameToken", new() { [1] = ThriftValue.String(session.Text(0)), [2] = ThriftValue.Int32(1) }, timeout.Token);
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", host.ConnectionPort, timeout.Token);
        var stream = client.GetStream();
        await Send(stream, "1", 2, ReadOnlyMemory<byte>.Empty, timeout.Token);
        var negotiation = ProtoObject.Read(await LocalSessionHost.ReadFrame(stream, timeout.Token), 128);
        Assert.Equal("1", ProtoObject.Read(negotiation.Bytes(2), 128).Text(1));
        var credential = new ArrayBufferWriter<byte>();
        ProtoWriter.Bytes(credential, 1, Encoding.UTF8.GetBytes(ticket.Text(0)));
        var authentication = new ArrayBufferWriter<byte>();
        ProtoWriter.Bytes(authentication, 3, credential.WrittenSpan);
        await Send(stream, "2", 3, authentication.WrittenMemory, timeout.Token);
        var response = ProtoObject.Read(await LocalSessionHost.ReadFrame(stream, timeout.Token), 128);
        var reply = ProtoObject.Read(response.Bytes(2), 128);
        var accepted = ProtoObject.Read(ProtoObject.Read(reply.Bytes(3), 128).Bytes(1), 128);
        Assert.Equal(7UL, accepted.UInt64(1));
        Assert.Equal(profile.Name, accepted.Text(2));
        Assert.Equal("0007", accepted.Text(3));
        var realms = ProtoObject.Read(accepted.Bytes(4), 128).Repeated(1).ToArray();
        Assert.Single(realms);
        var descriptor = ProtoObject.Read(ProtoObject.Read(realms[0].Bytes, 128).Bytes(1), 128);
        Assert.Equal(290UL, descriptor.UInt64(1));
        Assert.Equal(0UL, descriptor.UInt64(2));
        Assert.Equal(0UL, descriptor.UInt64(3));
        while (!phases.Contains("server-selection")) await Task.Delay(10, timeout.Token);
        using var replay = new TcpClient();
        await replay.ConnectAsync("127.0.0.1", host.ConnectionPort, timeout.Token);
        await Send(replay.GetStream(), "3", 3, authentication.WrittenMemory, timeout.Token);
        await Assert.ThrowsAsync<EndOfStreamException>(() => LocalSessionHost.ReadFrame(replay.GetStream(), timeout.Token));
    }
    [Fact]
    public async Task WrongLauncherSecretCannotObtainTicket()
    {
        var profile = new Profile(Guid.NewGuid(), "Test", 1);
        await using var host = new LocalSessionHost();
        using var registration = host.Register(profile, "en", _ => { });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", host.LauncherPort, deadline.Token);
        var codec = new ThriftBinaryCodec(new(65536, 8192, 128));
        await Assert.ThrowsAsync<EndOfStreamException>(() => Rpc(codec, client.GetStream(), "connect", new()
        { [1] = ThriftValue.String("dofus"), [2] = ThriftValue.String("main"), [3] = ThriftValue.Int32(1), [4] = ThriftValue.String("wrong") }, deadline.Token));
    }
    private static async Task<ThriftMessage> Rpc(ThriftBinaryCodec codec, NetworkStream stream, string method, Dictionary<short, ThriftValue> fields, CancellationToken token)
    {
        await stream.WriteAsync(codec.Encode(new(method, ThriftMessageKind.Call, 1, fields)), token);
        return await codec.ReadAsync(stream, token);
    }
    [Fact]
    public async Task SharedEndpointsKeepTwoAccountsLanguagesAndTicketsSeparate()
    {
        await using var host = new LocalSessionHost();
        using var first = host.Register(new(Guid.NewGuid(), "Alpha", 1), "en", _ => { });
        using var second = host.Register(new(Guid.NewGuid(), "Beta", 2), "pt", _ => { });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var tickets = await Task.WhenAll(Ticket(host, first, deadline.Token), Ticket(host, second, deadline.Token));
        Assert.NotEqual(tickets[0], tickets[1]);
        var accounts = await Task.WhenAll(Authenticate(host, tickets[0], deadline.Token), Authenticate(host, tickets[1], deadline.Token));
        Assert.Equal("Alpha", accounts[0].Text(2));
        Assert.Equal(1UL, accounts[0].UInt64(1));
        Assert.Equal("Beta", accounts[1].Text(2));
        Assert.Equal(2UL, accounts[1].UInt64(1));
        await Assert.ThrowsAsync<EndOfStreamException>(() => Authenticate(host, tickets[0], deadline.Token));
        var revoked = await Ticket(host, first, deadline.Token);
        first.Dispose();
        await Assert.ThrowsAsync<EndOfStreamException>(() => Authenticate(host, revoked, deadline.Token));
        var stillActive = await Authenticate(host, await Ticket(host, second, deadline.Token), deadline.Token);
        Assert.Equal("Beta", stillActive.Text(2));
    }
    [Fact]
    public async Task AnotherProfilesSecretCannotConnectAsTheRequestedAccount()
    {
        await using var host = new LocalSessionHost();
        using var first = host.Register(new(Guid.NewGuid(), "Alpha", 1), "en", _ => { });
        using var second = host.Register(new(Guid.NewGuid(), "Beta", 2), "pt", _ => { });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", host.LauncherPort, deadline.Token);
        var codec = new ThriftBinaryCodec(new(65536, 8192, 128));
        await Assert.ThrowsAsync<EndOfStreamException>(() => Rpc(codec, client.GetStream(), "connect", new()
        { [1] = ThriftValue.String("dofus"), [2] = ThriftValue.String("main"), [3] = ThriftValue.Int32(1), [4] = ThriftValue.String(second.ConnectSecret) }, deadline.Token));
    }
    [Fact]
    public async Task AuthenticatedConnectionsStayOpenWhileWaitingForTheUser()
    {
        await using var host = new LocalSessionHost(TimeSpan.FromSeconds(2));
        using var registration = host.Register(new(Guid.NewGuid(), "Waiting", 1), "en", _ => { });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var token = deadline.Token;
        using var launcher = new TcpClient();
        await launcher.ConnectAsync("127.0.0.1", host.LauncherPort, token);
        var codec = new ThriftBinaryCodec(new(65536, 8192, 128));
        var connected = await Rpc(codec, launcher.GetStream(), "connect", new()
        { [1] = ThriftValue.String("dofus"), [2] = ThriftValue.String("main"), [3] = ThriftValue.Int32(1), [4] = ThriftValue.String(registration.ConnectSecret) }, token);
        var key = connected.Text(0);
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", host.ConnectionPort, token);
        var credential = new ArrayBufferWriter<byte>();
        ProtoWriter.Bytes(credential, 1, Encoding.UTF8.GetBytes(registration.IssueTicket()));
        var authentication = new ArrayBufferWriter<byte>();
        ProtoWriter.Bytes(authentication, 3, credential.WrittenSpan);
        await Send(client.GetStream(), "1", 3, authentication.WrittenMemory, token);
        await LocalSessionHost.ReadFrame(client.GetStream(), token);

        // An authenticated window may wait at server selection longer than the handshake timeout.
        await Task.Delay(TimeSpan.FromSeconds(3), token);
        var language = await Rpc(codec, launcher.GetStream(), "settings_get", new()
        { [1] = ThriftValue.String(key), [2] = ThriftValue.String("language") }, token);
        Assert.Equal("en", language.Text(0));
        await Send(client.GetStream(), "2", 2, ReadOnlyMemory<byte>.Empty, token);
        await LocalSessionHost.ReadFrame(client.GetStream(), token);

        registration.Dispose();
        await Assert.ThrowsAsync<EndOfStreamException>(() => codec.ReadAsync(launcher.GetStream(), token));
        await Assert.ThrowsAsync<EndOfStreamException>(() => LocalSessionHost.ReadFrame(client.GetStream(), token));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnauthenticatedConnectionsStillTimeOut(bool launcherProtocol)
    {
        await using var host = new LocalSessionHost(TimeSpan.FromMilliseconds(200));
        using var client = new TcpClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync("127.0.0.1", launcherProtocol ? host.LauncherPort : host.ConnectionPort, deadline.Token);
        var buffer = new byte[1];
        Assert.Equal(0, await client.GetStream().ReadAsync(buffer, deadline.Token));
    }
    private static async Task<string> Ticket(LocalSessionHost host, LocalSessionHost.Session registration, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", host.LauncherPort, token);
        var codec = new ThriftBinaryCodec(new(65536, 8192, 128));
        var stream = client.GetStream();
        var connected = await Rpc(codec, stream, "connect", new()
        { [1] = ThriftValue.String("dofus"), [2] = ThriftValue.String("main"), [3] = ThriftValue.Int32((int)registration.Profile.Slot), [4] = ThriftValue.String(registration.ConnectSecret) }, token);
        var key = connected.Text(0);
        var language = await Rpc(codec, stream, "settings_get", new() { [1] = ThriftValue.String(key), [2] = ThriftValue.String("language") }, token);
        Assert.Equal(registration.Language, language.Text(0));
        var ticket = await Rpc(codec, stream, "auth_getGameToken", new() { [1] = ThriftValue.String(key), [2] = ThriftValue.Int32(1) }, token);
        return ticket.Text(0);
    }
    private static async Task<ProtoObject> Authenticate(LocalSessionHost host, string ticket, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", host.ConnectionPort, token);
        var credential = new ArrayBufferWriter<byte>();
        ProtoWriter.Bytes(credential, 1, Encoding.UTF8.GetBytes(ticket));
        var auth = new ArrayBufferWriter<byte>();
        ProtoWriter.Bytes(auth, 3, credential.WrittenSpan);
        await Send(client.GetStream(), "1", 3, auth.WrittenMemory, token);
        var response = ProtoObject.Read(await LocalSessionHost.ReadFrame(client.GetStream(), token), 128);
        var reply = ProtoObject.Read(response.Bytes(2), 128);
        return ProtoObject.Read(ProtoObject.Read(reply.Bytes(3), 128).Bytes(1), 128);
    }
    private static async Task Send(NetworkStream stream, string correlation, int operation, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        var request = new ArrayBufferWriter<byte>();
        ProtoWriter.Bytes(request, 1, Encoding.UTF8.GetBytes(correlation));
        ProtoWriter.Bytes(request, operation, data.Span);
        var envelope = new ArrayBufferWriter<byte>();
        ProtoWriter.Bytes(envelope, 1, request.WrittenSpan);
        var framed = new ArrayBufferWriter<byte>();
        ProtoWriter.Varint(framed, (ulong)envelope.WrittenCount);
        framed.Write(envelope.WrittenSpan);
        await stream.WriteAsync(framed.WrittenMemory, token);
    }
}
