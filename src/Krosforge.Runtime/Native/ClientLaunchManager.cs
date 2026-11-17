using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Krosforge.Contracts;
namespace Krosforge.Runtime.Native;

internal sealed class ClientLaunchManager(string dataRoot) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, LaunchJob> jobs = new();
    private readonly SemaphoreSlim preparation = new(1);
    private LocalSessionHost? host;
    private bool configured;
    internal bool HasActiveClients => jobs.Values.Any(job => job.Active);
    internal LaunchStatus Start(Profile profile, string source, string language)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("ErrorWindowsRequired");
        if (profile.Slot is < 1 or > 65535) throw new InvalidOperationException("ErrorProfileIdentity");
        if (language is not ("en" or "pt" or "fr" or "de" or "es")) language = "en";
        if (jobs.TryGetValue(profile.Id, out var existing) && existing.Active) throw new InvalidOperationException("ErrorClientAlreadyRunning");
        var job = new LaunchJob(profile.Id);
        jobs[profile.Id] = job;
        job.Work = Task.Run(() => Run(job, profile, source, language));
        return job.Status;
    }
    internal LaunchStatus Status(Guid profileId) => jobs.TryGetValue(profileId, out var job) ? job.Status : new(profileId, "idle", 0, 0);
    internal bool Cancel(Guid profileId)
    {
        if (jobs.TryGetValue(profileId, out var job) && job.Status.Stage == "preparing")
        {
            job.Cancellation.Cancel();
            return true;
        }
        return false;
    }
    private async Task Run(LaunchJob job, Profile profile, string source, string language)
    {
        LocalSessionHost.Session? session = null;
        var terminal = "exited";
        string? failure = null;
        try
        {
            string target;
            await preparation.WaitAsync(job.Cancellation.Token);
            try
            {
                var installation = ClientInstallation.Inspect(source);
                target = ClientPreparation.Target(dataRoot);
                if (!configured)
                    await SharedClientMigration.MigrateAsync(dataRoot, path => ValidatePrepared(installation.Path, path, allowLegacy: true), job.Cancellation.Token);
                if (!Directory.Exists(target))
                    target = await ClientPreparation.PrepareAsync(installation.Path, dataRoot, new ProgressSink(value => job.Update("preparing", value.BytesCopied, value.TotalBytes)), job.Cancellation.Token);
                ValidatePrepared(installation.Path, target);
                job.Cancellation.Token.ThrowIfCancellationRequested();
                host ??= new LocalSessionHost();
                if (!configured)
                {
                    await ClientLaunchConfiguration.WriteAsync(target, host.BaseUrl, job.Cancellation.Token);
                    configured = true;
                }
                session = host.Register(profile, language, stage => job.Update(stage));
            }
            finally { preparation.Release(); }
            var logDirectory = Path.Combine(dataRoot, "logs", profile.Id.ToString("N"));
            SafePaths.CheckTree(logDirectory);
            Directory.CreateDirectory(logDirectory);
            SafePaths.CheckTree(logDirectory);
            var logPath = Path.Combine(logDirectory, "client-" + Guid.NewGuid().ToString("N") + ".log");
            var start = new ProcessStartInfo(Path.Combine(target, "Dofus.exe")) { UseShellExecute = false, WorkingDirectory = target };
            start.Environment["ZAAP_PORT"] = host!.LauncherPort.ToString();
            start.Environment["ZAAP_GAME"] = "dofus";
            start.Environment["ZAAP_RELEASE"] = "main";
            start.Environment["ZAAP_INSTANCE_ID"] = profile.Slot.ToString();
            start.Environment["ZAAP_HASH"] = session.ConnectSecret;
            foreach (var argument in new[] { "-logFile", logPath, "--connectionPort", host!.ConnectionPort.ToString(), "--langCode", language, "--port", host!.LauncherPort.ToString(), "--gameName", "dofus", "--gameRelease", "main", "--instanceId", profile.Slot.ToString(), "--hash", session.ConnectSecret, "--canLogin", "true" }) start.ArgumentList.Add(argument);
            job.Cancellation.Token.ThrowIfCancellationRequested();
            job.Update("starting");
            using var process = Process.Start(start) ?? throw new IOException("ErrorLaunchFailed");
            job.Process = process;
            job.Update("starting", processId: process.Id);
            await process.WaitForExitAsync();
        }
        catch (OperationCanceledException) { terminal = "cancelled"; failure = "ErrorPreparationCancelled"; }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or JsonException)
        {
            terminal = "failed"; failure = error.Message.StartsWith("Error", StringComparison.Ordinal) ? error.Message : "ErrorLaunchFailed";
        }
        finally
        {
            session?.Dispose();
            job.Update(terminal, error: failure);
        }
    }
    internal static PreparedClient ValidatePrepared(string source, string target, bool allowLegacy = false)
    {
        ClientPreparation.EnsureDisjoint(source, target);
        SafePaths.CheckTree(target);
        foreach (var relative in new[] { "krosforge-client.json", "Dofus_Data/app.info", "Dofus_Data/globalgamemanagers" })
        {
            var path = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
            SafePaths.CheckTree(Path.GetDirectoryName(path)!);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("ErrorCopyInvalid");
        }
        var json = File.ReadAllText(Path.Combine(target, "krosforge-client.json"));
        var marker = JsonSerializer.Deserialize<PreparedClient>(json) ?? throw new IOException("ErrorCopyInvalid");
        if (marker.Build != ClientInstallation.SupportedBuild || marker.Company is not { Length: 6 } || marker.Product is not { Length: 5 }) throw new IOException("ErrorCopyInvalid");
        if (marker.Layout != 2)
        {
            using var legacy = JsonDocument.Parse(json);
            if (!allowLegacy || marker.Layout != 0 || marker.Company != "Krscli" || marker.Product[0] != 'P' ||
                !Guid.TryParseExact(Path.GetFileName(target), "N", out var id) ||
                !legacy.RootElement.TryGetProperty("ProfileId", out var owner) || !owner.TryGetGuid(out var ownerId) || ownerId != id)
                throw new IOException("ErrorCopyInvalid");
        }
        foreach (var (relative, hash) in ClientCompatibility.Hashes.Where(pair => !pair.Key.EndsWith("globalgamemanagers", StringComparison.Ordinal)))
        {
            var path = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
            SafePaths.CheckTree(Path.GetDirectoryName(path)!);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("ErrorCopyInvalid");
            using var file = File.OpenRead(path);
            if (Convert.ToHexStringLower(SHA256.HashData(file)) != hash) throw new IOException("ErrorCopyInvalid");
        }
        var info = File.ReadAllLines(Path.Combine(target, "Dofus_Data", "app.info"));
        if (info.Length != 2 || info[0] != marker.Company || info[1] != marker.Product) throw new IOException("ErrorCopyInvalid");
        var originalInfo = File.ReadAllLines(Path.Combine(source, "Dofus_Data", "app.info"));
        var expected = File.ReadAllBytes(Path.Combine(source, "Dofus_Data", "globalgamemanagers"));
        ClientPreparation.ReplaceUnique(expected, Serialize(originalInfo[0]).Concat(Serialize(originalInfo[1])).ToArray(), Serialize(marker.Company).Concat(Serialize(marker.Product)).ToArray());
        var actual = File.ReadAllBytes(Path.Combine(target, "Dofus_Data", "globalgamemanagers"));
        if (!expected.AsSpan().SequenceEqual(actual)) throw new IOException("ErrorCopyInvalid");
        return marker;
    }
    private static byte[] Serialize(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return BitConverter.GetBytes(bytes.Length).Concat(bytes).Concat(new byte[(4 - bytes.Length % 4) % 4]).ToArray();
    }
    public void Dispose()
    {
        foreach (var job in jobs.Values) job.Cancellation.Cancel();
        foreach (var job in jobs.Values)
        {
            job.Work?.GetAwaiter().GetResult();
            job.Cancellation.Dispose();
        }
        host?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        preparation.Dispose();
    }
    private sealed class ProgressSink(Action<CopyProgress> report) : IProgress<CopyProgress>
    {
        public void Report(CopyProgress value) => report(value);
    }
    private sealed class LaunchJob(Guid profileId)
    {
        private LaunchStatus status = new(profileId, "preparing", 0, 0);
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Process? Process { get; set; }
        internal Task? Work { get; set; }
        internal LaunchStatus Status => Volatile.Read(ref status);
        internal bool Active => Status.Stage is "preparing" or "starting" or "authenticating" or "server-selection";
        internal void Update(string stage, long? bytes = null, long? total = null, string? error = null, int? processId = null)
        {
            var old = Status;
            Volatile.Write(ref status, new(profileId, stage, bytes ?? old.BytesCopied, total ?? old.TotalBytes, error, processId ?? old.ProcessId));
        }
    }
}
