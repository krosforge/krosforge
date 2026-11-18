using Krosforge.Contracts;
using Krosforge.Runtime.Native;
using System.Text.Json;
namespace Krosforge.Runtime;

public sealed class RuntimeService : IDisposable
{
    private readonly ProfileStore store;
    private readonly string directory;
    private readonly ClientLaunchManager launches;
    public bool HasActiveClients => launches.HasActiveClients;
    public RuntimeService(string directory)
    {
        this.directory = directory;
        SafePaths.CheckTree(directory);
        store = new(directory);
        launches = new(directory);
        Cleanup();
    }
    public Response Handle(Request request)
    {
        if (request.Version != Wire.Version)
            return Response.Fail("ErrorProtocolVersion");
        try
        {
            switch (request.Operation)
            {
                case "ping": return Response.Ok(true);
                case "snapshot":
                    var installation = ClientInstallation.Discover(store.Setting("installation"));
                    return Response.Ok(new Snapshot(store.List(), installation, HasActiveClients, Server()));
                case "configure-server":
                    if (HasActiveClients) return Response.Fail("ErrorCloseGameFirst");
                    var name = Krosforge.Core.ServerName.Normalize(request.Value ?? "");
                    var renamed = Server() with { Name = name };
                    SaveServer(renamed);
                    return Response.Ok(renamed);
                case "configure-server-settings":
                    if (HasActiveClients) return Response.Fail("ErrorCloseGameFirst");
                    ServerSettings settings;
                    try { settings = JsonSerializer.Deserialize<ServerSettings>(request.Value ?? "") ?? throw new ArgumentException("ErrorServerRates"); }
                    catch (JsonException) { throw new ArgumentException("ErrorServerRates"); }
                    settings = ValidateServer(settings);
                    SaveServer(settings);
                    return Response.Ok(settings);
                case "installation":
                    var selected = ClientInstallation.Inspect(request.Value ?? "");
                    store.SetSetting("installation", selected.Path);
                    return Response.Ok(selected);
                case "create":
                    return Response.Ok(store.Create(request.Value ?? ""));
                case "rename":
                    store.Rename(RequireId(request), request.Value ?? "");
                    return Response.Ok(true);
                case "delete":
                    if (launches.IsActive(RequireId(request))) return Response.Fail("ErrorCloseGameFirst");
                    store.Delete(RequireId(request), request.Confirmation ?? "");
                    Cleanup();
                    return Response.Ok(true);
                case "launch":
                    var profile = store.List().SingleOrDefault(p => p.Id == RequireId(request));
                    if (profile is null) return Response.Fail("ErrorProfileNotFound");
                    return Response.Ok(launches.Start(profile, store.Setting("installation") ?? ClientInstallation.DefaultPath, request.Value ?? "en", Server()));
                case "launch-status": return Response.Ok(launches.Status(RequireId(request)));
                case "cancel-launch": return Response.Ok(launches.Cancel(RequireId(request)));
                default:
                    return Response.Fail("ErrorUnknownOperation");
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { return Response.Fail("ErrorStorage"); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Response.Fail(error.Message);
        }
    }
    private ServerSettings Server()
    {
        var saved = store.Setting("server.configuration");
        if (saved is null) return new(Krosforge.Core.ServerName.Normalize(store.Setting("server.name") ?? "Krosforge"));
        try { return ValidateServer(JsonSerializer.Deserialize<ServerSettings>(saved) ?? throw new InvalidDataException("ErrorServerRates")); }
        catch (JsonException) { throw new InvalidDataException("ErrorServerRates"); }
    }
    private static ServerSettings ValidateServer(ServerSettings settings)
    {
        if (settings.ExperienceMultiplier is < 0 or > 1000 || settings.DropMultiplier is < 0 or > 1000)
            throw new ArgumentException("ErrorServerRates");
        return settings with { Name = Krosforge.Core.ServerName.Normalize(settings.Name ?? "") };
    }
    private void SaveServer(ServerSettings settings) => store.SetSetting("server.configuration", JsonSerializer.Serialize(settings));
    private static Guid RequireId(Request r) => r.ProfileId is { } id && id != Guid.Empty ? id : throw new ArgumentException("ErrorInvalidProfile");
    private void Cleanup()
    {
        foreach (var id in store.PendingCleanup())
            try
            {
                SafePaths.DeleteOwnedProfile(directory, id);
                store.CompleteCleanup(id);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose() { launches.Dispose(); store.Dispose(); }
}
