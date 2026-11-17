using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Krosforge.Contracts;
using Krosforge.Runtime.Native.Protocol;
using Krosforge.Runtime.Native.Protocol.Connection;
using Krosforge.Runtime.Native.Protocol.Launcher;
using Krosforge.Runtime.Native.Protocol.Protobuf;
namespace Krosforge.Runtime.Native;

internal sealed class LocalSessionHost : IAsyncDisposable
{
    private readonly Profile profile;
    private readonly string language;
    private readonly Action<string> phase;
    private readonly CancellationTokenSource lifetime = new();
    private readonly TcpListener http = new(IPAddress.Loopback, 0);
    private readonly TcpListener launcher = new(IPAddress.Loopback, 0);
    private readonly TcpListener connection = new(IPAddress.Loopback, 0);
    private readonly List<Task> listeners = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Task> clients = new();
    private readonly SemaphoreSlim slots = new(24);
    private readonly string sessionKey = Secret();
    private readonly object ticketGate = new();
    private string? ticket;
    private DateTimeOffset ticketExpires;
    private int nextClient;
    internal string ConnectSecret { get; } = Secret();
    internal int HttpPort => ((IPEndPoint)http.LocalEndpoint).Port;
    internal int LauncherPort => ((IPEndPoint)launcher.LocalEndpoint).Port;
    internal int ConnectionPort => ((IPEndPoint)connection.LocalEndpoint).Port;
    internal string BaseUrl => $"http://127.0.0.1:{HttpPort}";
    internal LocalSessionHost(Profile profile, string language, Action<string> phase)
    {
        this.profile = profile;
        this.language = language;
        this.phase = phase;
        try
        {
            http.Start(); launcher.Start(); connection.Start();
            listeners.Add(Accept(http, ServeHttp));
            listeners.Add(Accept(launcher, ServeLauncher));
            listeners.Add(Accept(connection, ServeConnection));
        }
        catch { http.Stop(); launcher.Stop(); connection.Stop(); lifetime.Cancel(); throw; }
    }
    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
    private async Task Accept(TcpListener listener, Func<NetworkStream, CancellationToken, Task> serve)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                if (!slots.Wait(0)) { client.Dispose(); continue; }
                var id = Interlocked.Increment(ref nextClient);
                var task = Handle(client, serve);
                clients[id] = task;
                _ = task.ContinueWith(_ => clients.TryRemove(id, out var ignored), TaskScheduler.Default);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task Handle(TcpClient client, Func<NetworkStream, CancellationToken, Task> serve)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            try { await serve(client.GetStream(), deadline.Token); }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ProtocolException or JsonException) { }
            finally { slots.Release(); }
        }
    }
    private async Task ServeHttp(NetworkStream stream, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var header = new List<byte>();
        var next = new byte[1];
        while (header.Count < 8192)
        {
            await stream.ReadExactlyAsync(next, deadline.Token);
            header.Add(next[0]);
            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
        }
        if (header.Count >= 8192) return;
        var request = Encoding.ASCII.GetString(header.ToArray()).Split('\r')[0].Split(' ');
        if (request.Length != 3 || request[0] != "GET") return;
        var path = request[1].Split('?')[0];
        string body;
        var type = "application/json";
        var status = "200 OK";
        switch (path)
        {
            case "/config.json":
                body = JsonSerializer.Serialize(new
                {
                    gameAppId = 1, connectionHosts = new[] { $"local:127.0.0.1:{ConnectionPort}" },
                    buildType = 1, chatAppId = 1, chatServerHost = "127.0.0.1", chatServerPort = HttpPort,
                    haapiAnkamaUrl = BaseUrl + "/haapi/ankama/", haapiDofusUrl = BaseUrl + "/haapi/dofus/",
                    shopiDofusUrl = BaseUrl + "/shop/", webShopDofusUrl = BaseUrl + "/shop/", gamesActivityDescriptorUrl = BaseUrl + "/activity.json",
                    versionFileUrl = BaseUrl + "/version", dofusWebsiteUrl = BaseUrl + "/", cinematicsUrl = BaseUrl + "/cinematics/"
                }); break;
            case "/version": body = $"Version={ClientInstallation.SupportedBuild}\nConfigUrl={BaseUrl}/config.json\n"; type = "text/plain"; break;
            case "/activity.json": body = JsonSerializer.Serialize(new { games = Array.Empty<object>(), launcher = new { name = "launcher", id = 0, languages = new[] { language }, fallback = language, basePath = BaseUrl + "/activities/" } }); break;
            default: body = "{}"; status = path == $"/activities/{language}.json" ? "200 OK" : "404 Not Found"; break;
        }
        var bytes = Encoding.UTF8.GetBytes(body);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n"), deadline.Token);
        await stream.WriteAsync(bytes, deadline.Token);
    }
    private async Task ServeLauncher(NetworkStream stream, CancellationToken token)
    {
        var codec = new ThriftBinaryCodec(new(65536, 8192, 128));
        var settings = new Dictionary<string, string> { ["language"] = language, ["lang"] = language, ["langCode"] = language };
        while (!token.IsCancellationRequested)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            var request = await codec.ReadAsync(stream, deadline.Token);
            ThriftValue? value;
            if (request.Kind != ThriftMessageKind.Call) throw new ProtocolException("Launcher call required.");
            if (request.Method == "connect")
            {
                if (request.Text(1) != "dofus" || request.Text(2) != "main" || request.Int32(3) != profile.Slot || !Equal(request.Text(4), ConnectSecret)) throw new ProtocolException("Launcher rejected.");
                value = ThriftValue.String(sessionKey);
                phase("authenticating");
            }
            else
            {
                if (!Equal(request.Text(1), sessionKey)) throw new ProtocolException("Launcher session rejected.");
                switch (request.Method)
                {
                    case "auth_getGameToken":
                        request.Int32(2);
                        lock (ticketGate) { ticket = Secret(); ticketExpires = DateTimeOffset.UtcNow.AddMinutes(2); value = ThriftValue.String(ticket); }
                        break;
                    case "settings_get": value = ThriftValue.String(settings.GetValueOrDefault(request.Text(2), "")); break;
                    case "settings_set":
                        var key = request.Text(2); var setting = request.Text(3);
                        if (key.Length > 128 || setting.Length > 4096 || (!settings.ContainsKey(key) && settings.Count >= 32)) throw new ProtocolException("Setting rejected.");
                        settings[key] = setting; value = null; break;
                    case "userInfo_get": value = ThriftValue.String(JsonSerializer.Serialize(new { dhzc = profile.Slot, dhze = profile.Name, dhzf = profile.Slot.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) })); break;
                    case "hasGameAccess": request.Int32(2); value = ThriftValue.Bool(true); break;
                    case "hasPremiumAccess": case "updater_isUpdateAvailable": case "zaapMustUpdate_get": value = ThriftValue.Bool(false); break;
                    case "zaapVersion_get": value = ThriftValue.String("0.0.1"); break;
                    default: throw new ProtocolException("Unsupported launcher method.");
                }
            }
            var response = new ThriftMessage(request.Method, ThriftMessageKind.Reply, request.Sequence,
                value is null ? new Dictionary<short, ThriftValue>() : new Dictionary<short, ThriftValue> { [0] = value });
            await stream.WriteAsync(codec.Encode(response), deadline.Token);
        }
    }
    private async Task ServeConnection(NetworkStream stream, CancellationToken token)
    {
        var envelope = new ConnectionEnvelopeCodec(new(1, 2, 1, 2, 2), 128);
        var authenticated = false;
        while (!token.IsCancellationRequested)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(authenticated ? TimeSpan.FromMinutes(15) : TimeSpan.FromSeconds(30));
            var request = envelope.DecodeRequest(await ReadFrame(stream, deadline.Token));
            byte[] payload;
            if (request.Operation == ConnectionAuthentication.Authenticate)
            {
                if (authenticated) throw new ProtocolException("Already authenticated.");
                var supplied = ConnectionAuthentication.ReadTicket(request.Payload.Span);
                lock (ticketGate)
                {
                    if (ticket is null || DateTimeOffset.UtcNow > ticketExpires || !Equal(supplied, ticket)) throw new ProtocolException("Ticket rejected.");
                    ticket = null;
                }
                authenticated = true;
                await Task.Delay(250, deadline.Token);
                payload = envelope.EncodeResponse(request.CorrelationId, request.Operation, ConnectionAuthentication.Accepted(profile));
            }
            else if (request.Operation == ConnectionAuthentication.SelectRealm)
            {
                if (!authenticated) throw new ProtocolException("Authentication required.");
                payload = envelope.EncodeResponse(request.CorrelationId, request.Operation, ConnectionAuthentication.RealmUnavailable());
            }
            else payload = envelope.ReplyToProtocolNegotiation(request);
            var output = new ArrayBufferWriter<byte>();
            ProtoWriter.Varint(output, (ulong)payload.Length);
            output.Write(payload);
            await stream.WriteAsync(output.WrittenMemory, deadline.Token);
            if (request.Operation == ConnectionAuthentication.Authenticate) phase("server-selection");
        }
    }
    internal static async Task<byte[]> ReadFrame(Stream stream, CancellationToken token)
    {
        var next = new byte[1];
        uint length = 0;
        for (var index = 0; index < 5; index++)
        {
            await stream.ReadExactlyAsync(next, token);
            if (index == 4 && next[0] > 15) throw new ProtocolException("Invalid frame length.");
            length |= (uint)(next[0] & 127) << (7 * index);
            if ((next[0] & 128) != 0) continue;
            if (length == 0 || length > 65536) throw new ProtocolException("Frame size rejected.");
            var data = new byte[length];
            await stream.ReadExactlyAsync(data, token);
            return data;
        }
        throw new ProtocolException("Frame length rejected.");
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        http.Stop(); launcher.Stop(); connection.Stop();
        await Task.WhenAll(listeners);
        await Task.WhenAll(clients.Values);
        slots.Dispose(); lifetime.Dispose();
    }
}
