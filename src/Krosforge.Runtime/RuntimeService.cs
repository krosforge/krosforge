using Krosforge.Contracts;
namespace Krosforge.Runtime;

public sealed class RuntimeService : IDisposable
{
    private readonly ProfileStore store;
    private readonly string directory;
    public RuntimeService(string directory)
    {
        this.directory = directory;
        SafePaths.CheckTree(directory);
        store = new(directory);
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
                    return Response.Ok(new Snapshot(store.List(), installation));
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
                    store.Delete(RequireId(request), request.Confirmation ?? "");
                    Cleanup();
                    return Response.Ok(true);
                case "launch":
                    if (!store.List().Any(p => p.Id == RequireId(request)))
                        return Response.Fail("ErrorProfileNotFound");
                    if (ClientInstallation.Discover(store.Setting("installation")) is null)
                        return Response.Fail("ErrorSelectInstallation");
                    return Response.Fail("ErrorSessionFailed");
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
    public void Dispose() => store.Dispose();
}
