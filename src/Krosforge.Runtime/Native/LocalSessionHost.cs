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

internal sealed partial class LocalSessionHost : IAsyncDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, Session> sessions = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TcpListener http = new(IPAddress.Loopback, 0);
    private readonly TcpListener launcher = new(IPAddress.Loopback, 0);
    private readonly TcpListener connection = new(IPAddress.Loopback, 0);
    private readonly TcpListener world = new(IPAddress.Loopback, 0);
    private readonly List<Task> listeners = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Task> clients = new();
    private readonly SemaphoreSlim slots = new(24);
    private readonly TimeSpan authenticationTimeout;
    private readonly Action<string>? diagnostic;
    private readonly TimeProvider clock;
    private readonly Func<NetworkStream, byte[], CancellationToken, Task> writeTransfer;

    private readonly CharacterService? characters;
    private readonly WorldEntryService? worldEntry;
    private int nextClient;
    internal int WorldPort => ((IPEndPoint)world.LocalEndpoint).Port;
    internal int HttpPort => ((IPEndPoint)http.LocalEndpoint).Port;
    internal int LauncherPort => ((IPEndPoint)launcher.LocalEndpoint).Port;
    internal int ConnectionPort => ((IPEndPoint)connection.LocalEndpoint).Port;
    internal string BaseUrl => $"http://127.0.0.1:{HttpPort}";
    internal LocalSessionHost(TimeSpan? authenticationTimeout = null, Action<string>? diagnostic = null, TimeProvider? clock = null,
        Func<NetworkStream, byte[], CancellationToken, Task>? writeTransfer = null, CharacterService? characters = null, WorldEntryService? worldEntry = null)
    {
        this.characters = characters;
        this.worldEntry = worldEntry;
        this.diagnostic = diagnostic;
        this.clock = clock ?? TimeProvider.System;
        this.writeTransfer = writeTransfer ?? ((stream, payload, token) => WorldProtocol.Write(stream, payload, token));
        this.authenticationTimeout = authenticationTimeout ?? TimeSpan.FromSeconds(30);
        try
        {
            http.Start(); launcher.Start(); connection.Start(); world.Start();
            listeners.Add(Accept(http, ServeHttp));
            listeners.Add(Accept(launcher, ServeLauncher));
            listeners.Add(Accept(connection, ServeConnection));
            listeners.Add(Accept(world, ServeWorld));
        }
        catch { http.Stop(); launcher.Stop(); connection.Stop(); world.Stop(); lifetime.Cancel(); throw; }
    }
    internal Session Register(Profile profile, string language, Action<string> phase)
    {
        var session = new Session(this, profile, language, phase);
        if (!sessions.TryAdd(profile.Slot, session)) throw new InvalidOperationException("ErrorClientAlreadyRunning");
        return session;
    }
    internal sealed class Session(LocalSessionHost owner, Profile profile, string language, Action<string> phase) : IDisposable
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource lifetime = new();
        private string? ticket;
        private DateTimeOffset ticketExpires;
        internal SessionTransfer Transfer { get; } = new(owner.clock);
        internal Profile Profile { get; } = profile;
        internal string Language { get; } = language;
        internal string ConnectSecret { get; } = Secret();
        internal string Key { get; } = Secret();
        internal CancellationToken Token => lifetime.Token;
        internal void ReleaseNetwork(long lease, string? expectedPhase = null)
        {
            lock (gate) if (!lifetime.IsCancellationRequested && Transfer.Release(lease, expectedPhase)) phase("disconnected");
        }
        internal void Phase(string value) { lock (gate) { if (!lifetime.IsCancellationRequested) { owner.diagnostic?.Invoke("phase " + value); phase(value); } } }
        internal string IssueTicket()
        {
            lock (gate)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                ticketExpires = DateTimeOffset.UtcNow.AddMinutes(2);
                return ticket = Secret();
            }
        }
        internal bool ConsumeTicket(string supplied)
        {
            lock (gate)
            {
                if (lifetime.IsCancellationRequested || ticket is null || DateTimeOffset.UtcNow > ticketExpires || !Equal(supplied, ticket)) return false;
                ticket = null;
                return true;
            }
        }
        public void Dispose()
        {
            lock (gate)
            {
                if (lifetime.IsCancellationRequested) return;
                lifetime.Cancel();
                Transfer.Dispose();
                ticket = null;
                owner.sessions.TryRemove(Profile.Slot, out _);
            }
        }
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
        {
            // Each protocol bounds unauthenticated requests. Active sessions live until their
            // registration is revoked or the host shuts down, not a fixed connection age.
            try { await serve(client.GetStream(), lifetime.Token); }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ProtocolException or JsonException or Microsoft.Data.Sqlite.SqliteException) { diagnostic?.Invoke("connection-ended " + e.GetType().Name); }
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
            case "/activity.json": body = JsonSerializer.Serialize(new { games = Array.Empty<object>(), launcher = new { name = "launcher", id = 0, languages = new[] { "en", "fr", "de", "es", "pt" }, fallback = "en", basePath = BaseUrl + "/activities/" } }); break;
            default: body = "{}"; status = new[] { "/activities/en.json", "/activities/fr.json", "/activities/de.json", "/activities/es.json", "/activities/pt.json" }.Contains(path) ? "200 OK" : "404 Not Found"; break;
        }
        var bytes = Encoding.UTF8.GetBytes(body);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n"), deadline.Token);
        await stream.WriteAsync(bytes, deadline.Token);
    }
    private async Task ServeLauncher(NetworkStream stream, CancellationToken token)
    {
        var codec = new ThriftBinaryCodec(new(65536, 8192, 128));
        Session? session = null;
        var settings = new Dictionary<string, string>();
        while (!token.IsCancellationRequested)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, session?.Token ?? token);
            if (session is null) deadline.CancelAfter(authenticationTimeout);
            var request = await codec.ReadAsync(stream, deadline.Token);
            ThriftValue? value;
            if (request.Kind != ThriftMessageKind.Call) throw new ProtocolException("Launcher call required.");
            if (request.Method == "connect")
            {
                if (session is not null || request.Text(1) != "dofus" || request.Text(2) != "main" || !sessions.TryGetValue(request.Int32(3), out session) || !Equal(request.Text(4), session.ConnectSecret)) throw new ProtocolException("Launcher rejected.");
                settings["language"] = settings["lang"] = settings["langCode"] = session.Language;
                value = ThriftValue.String(session.Key);
                session.Phase("authenticating");
            }
            else
            {
                if (session is null || session.Token.IsCancellationRequested || !Equal(request.Text(1), session.Key)) throw new ProtocolException("Launcher session rejected.");
                switch (request.Method)
                {
                    case "auth_getGameToken":
                        request.Int32(2);
                        value = ThriftValue.String(session.IssueTicket());
                        break;
                    case "settings_get": value = ThriftValue.String(settings.GetValueOrDefault(request.Text(2), "")); break;
                    case "settings_set":
                        var key = request.Text(2); var setting = request.Text(3);
                        if (key.Length > 128 || setting.Length > 4096 || (!settings.ContainsKey(key) && settings.Count >= 32)) throw new ProtocolException("Setting rejected.");
                        settings[key] = setting; value = null; break;
                    case "userInfo_get": value = ThriftValue.String(JsonSerializer.Serialize(new { dhzc = session.Profile.Slot, dhze = session.Profile.Name, dhzf = session.Profile.Slot.ToString("D4", System.Globalization.CultureInfo.InvariantCulture) })); break;
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
        Session? session = null;
        long lease = 0;
        bool transferred = false;
        bool handoffIssued = false;
        using var authentication = CancellationTokenSource.CreateLinkedTokenSource(token);
        authentication.CancelAfter(authenticationTimeout);
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session is null ? authentication.Token : token, session?.Token ?? token);
                var request = envelope.DecodeRequest(await ReadFrame(stream, deadline.Token));
                byte[] payload;
                if (request.Operation == ConnectionAuthentication.Authenticate)
                {
                    if (session is not null) throw new ProtocolException("Already authenticated.");
                    var supplied = ConnectionAuthentication.ReadTicket(request.Payload.Span);
                    foreach (var candidate in sessions.Values)
                    {
                        lease = candidate.Transfer.Attach(supplied, returning: true);
                        if (lease == 0 && candidate.ConsumeTicket(supplied)) lease = candidate.Transfer.Open();
                        if (lease != 0) { session = candidate; break; }
                    }
                    if (session is null) throw new ProtocolException("Ticket rejected.");
                    await Task.Delay(250, deadline.Token);
                    session.Token.ThrowIfCancellationRequested();
                    payload = envelope.EncodeResponse(request.CorrelationId, request.Operation, ConnectionAuthentication.Accepted(session.Profile, characters?.List(session.Profile.Id)));
                }
                else if (request.Operation == ConnectionAuthentication.SelectRealm)
                {
                    if (session is null || !session.Transfer.Current(lease, "connection")) throw new ProtocolException("Authentication required.");
                    var selection = ProtoObject.Read(request.Payload.Span, 128); selection.Only(1);
                    if (selection.UInt64(1) != ServerPresentation.RealmId)
                        payload = envelope.EncodeResponse(request.CorrelationId, request.Operation, ConnectionAuthentication.RealmUnavailable());
                    else
                    {
                        var ticket = session.Transfer.Handoff(lease) ?? throw new ProtocolException("Transfer rejected.");
                        payload = envelope.EncodeResponse(request.CorrelationId, request.Operation, WorldProtocol.Selected(ticket, WorldPort));
                        handoffIssued = true;
                        session.Phase("connecting-server");
                        await writeTransfer(stream, payload, deadline.Token);
                        transferred = true;
                        return;
                    }
                }
                else payload = envelope.ReplyToProtocolNegotiation(request);
                await WorldProtocol.Write(stream, payload, deadline.Token);
                if (request.Operation == ConnectionAuthentication.Authenticate) session!.Phase("server-selection");
            }
        }
        finally
        {
            if (session is not null && !transferred)
            {
                session.ReleaseNetwork(lease, handoffIssued ? "handoff" : "connection");
            }
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
        foreach (var session in sessions.Values) session.Dispose();
        await lifetime.CancelAsync();
        http.Stop(); launcher.Stop(); connection.Stop(); world.Stop();
        await Task.WhenAll(listeners);
        await Task.WhenAll(clients.Values);
        slots.Dispose(); lifetime.Dispose();
    }
}
