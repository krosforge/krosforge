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
        await using var host = new LocalSessionHost(profile, "en", phases.Enqueue);
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
        { [1] = ThriftValue.String("dofus"), [2] = ThriftValue.String("main"), [3] = ThriftValue.Int32(7), [4] = ThriftValue.String(host.ConnectSecret) }, timeout.Token);
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
        Assert.Equal(2, ProtoObject.Read(accepted.Bytes(4), 128).Repeated(1).Count());
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
        await using var host = new LocalSessionHost(profile, "en", _ => { });
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
