using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using Krosforge.Runtime;
namespace Krosforge.Launcher.Updates;

internal sealed record UpdatePlan(string Target, string CandidateHash, string CurrentHash, int ParentId, long ParentStartedUtcTicks, bool Installed);
internal static class UpdateInstaller
{
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
    internal static string StagingRoot => Path.Combine(Path.GetTempPath(), "KrosforgeUpdates");
    public static string NewStagingDirectory()
    {
        var directory = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        SafePaths.CheckTree(directory);
        Directory.CreateDirectory(directory);
        return directory;
    }
    internal static string ValidateDirectory(string directory)
    {
        directory = UpdatePaths.Normalize(directory);
        if (!string.Equals(Path.GetDirectoryName(directory), UpdatePaths.Normalize(StagingRoot), StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
            throw new InvalidDataException("Invalid update staging directory.");
        SafePaths.CheckTree(directory);
        return directory;
    }
    private static void CheckFile(string file)
    {
        SafePaths.CheckTree(Path.GetDirectoryName(file)!);
        if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Update files cannot be links.");
    }
    public static async Task StartHelperAsync(DownloadedUpdate download, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var directory = ValidateDirectory(download.Directory);
        var target = UpdatePaths.Normalize(Environment.ProcessPath ?? throw new IOException("Application path unavailable."));
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(target).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Updates require the packaged application.");
        CheckFile(target);
        using var current = Process.GetCurrentProcess();
        var plan = new UpdatePlan(target, download.ExecutableHash, await ReleaseClient.HashAsync(target, token), current.Id, current.StartTime.ToUniversalTime().Ticks, Environment.GetCommandLineArgs().Contains("--installed"));
        var helper = Path.Combine(directory, "UpdateHost.exe");
        File.Copy(target, helper, false);
        await File.WriteAllTextAsync(Path.Combine(directory, "update.json"), JsonSerializer.Serialize(plan), token);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(directory);
        using var process = Process.Start(start) ?? throw new IOException("Could not start update helper.");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            while (!File.Exists(Path.Combine(directory, "ready")))
            {
                if (process.HasExited) throw new IOException("Update helper did not start.");
                await Task.Delay(100, deadline.Token);
            }
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            try { await File.WriteAllTextAsync(Path.Combine(directory, "cancel"), "", CancellationToken.None); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            try
            {
                using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(exitDeadline.Token);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync();
            }
            throw;
        }
    }
    public static async Task<int> RunHelperAsync(string directory)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        // Return loader errors to the helper instead of waiting on an unattended Windows dialog.
        SetErrorMode(0x0001 | 0x0002 | 0x8000);
        UpdatePlan? plan = null;
        var replaced = false;
        var verified = false;
        string? backup = null;
        string? ownedLockPath = null;
        var phase = "staging-directory";
        try
        {
            directory = ValidateDirectory(directory);
            phase = "update-plan";
            CheckFile(Path.Combine(directory, "update.json"));
            plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(Path.Combine(directory, "update.json"))) ?? throw new InvalidDataException("Missing update plan.");
            var target = UpdatePaths.Normalize(plan.Target);
            CheckFile(target);
            phase = "helper-image";
            var self = UpdatePaths.Normalize(Environment.ProcessPath!);
            if (!UpdatePaths.Same(self, Path.Combine(directory, "UpdateHost.exe")) || !string.Equals(await ReleaseClient.HashAsync(self, CancellationToken.None), plan.CurrentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid update helper.");
            phase = "parent-process";
            using var parent = Process.GetProcessById(plan.ParentId);
            if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartedUtcTicks || parent.MainModule?.FileName is not { } parentImage || !UpdatePaths.Same(parentImage, target))
                throw new InvalidDataException("The launcher process changed.");
            phase = "target-image";
            if (!string.Equals(await ReleaseClient.HashAsync(target, CancellationToken.None), plan.CurrentHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The installed executable changed.");
            phase = "candidate-image";
            var candidate = Path.Combine(directory, "Krosforge.exe");
            CheckFile(candidate);
            if (!string.Equals(await ReleaseClient.HashAsync(candidate, CancellationToken.None), plan.CandidateHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update changed after download.");
            verified = true;
            await File.WriteAllTextAsync(Path.Combine(directory, "ready"), "");
            await File.WriteAllTextAsync(Path.Combine(directory, "state"), "waiting-parent");
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!parent.HasExited)
            {
                if (File.Exists(Path.Combine(directory, "cancel")) || DateTime.UtcNow > deadline) return 3;
                await Task.Delay(100);
            }
            if (File.Exists(Path.Combine(directory, "cancel"))) return 3;
            var lockPath = target + ".update.lock";
            using (var ownership = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                ownedLockPath = lockPath;
                await File.WriteAllTextAsync(Path.Combine(directory, "state"), "replacing");
                backup = BackupPath(target, directory);
                await ReplaceAsync(plan, directory, CancellationToken.None);
                replaced = true;
                File.Delete(Path.Combine(directory, "started"));
                await File.WriteAllTextAsync(Path.Combine(directory, "state"), "starting");
                using var restarted = Process.Start(RestartInfo(plan, directory, false)) ?? throw new IOException("Could not restart the launcher.");
                await File.WriteAllTextAsync(Path.Combine(directory, "state"), "waiting-startup");
                var startupDeadline = DateTime.UtcNow.AddSeconds(45);
                while (!File.Exists(Path.Combine(directory, "started")))
                {
                    if (restarted.HasExited) throw new IOException("The new launcher did not acknowledge startup.");
                    if (DateTime.UtcNow >= startupDeadline)
                    {
                        restarted.Kill();
                        using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        await restarted.WaitForExitAsync(exitDeadline.Token);
                        throw new IOException("The new launcher did not acknowledge startup.");
                    }
                    await Task.Delay(100);
                }
                if (restarted.HasExited) throw new IOException("The new launcher exited unexpectedly.");
                try { File.Delete(backup); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            try { File.Delete(lockPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await File.WriteAllTextAsync(Path.Combine(directory, "completed"), "");
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            try { Console.Error.WriteLine($"Update helper failed during {phase}: {error.GetType().Name}: {error.Message}"); }
            catch (IOException) { }
            if (plan is not null && replaced && backup is not null && File.Exists(backup))
            {
                try { File.Replace(backup, plan.Target, null); }
                catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException) { return 5; }
            }
            if (verified && plan is not null && !IsParentRunning(plan))
            {
                try { await File.WriteAllTextAsync(Path.Combine(directory, "completed"), ""); using var restart = Process.Start(RestartInfo(plan, directory, true)); }
                catch (Exception restartError) when (restartError is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
            return 4;
        }
        finally
        {
            if (ownedLockPath is not null)
            {
                try { File.Delete(ownedLockPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
    private static bool IsParentRunning(UpdatePlan plan)
    {
        try { using var process = Process.GetProcessById(plan.ParentId); return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == plan.ParentStartedUtcTicks; }
        catch (ArgumentException) { return false; }
    }
    private static ProcessStartInfo RestartInfo(UpdatePlan plan, string directory, bool failed)
    {
        var start = new ProcessStartInfo(plan.Target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan.Target)! };
        if (plan.Installed) start.ArgumentList.Add("--installed");
        start.ArgumentList.Add("--cleanup-update"); start.ArgumentList.Add(directory);
        if (failed) start.ArgumentList.Add("--update-failed");
        return start;
    }
    internal static string BackupPath(string target, string directory) => Path.Combine(Path.GetDirectoryName(target)!, ".krosforge-backup-" + Path.GetFileName(directory) + ".exe");
    internal static async Task ReplaceAsync(UpdatePlan plan, string directory, CancellationToken token)
    {
        CheckFile(plan.Target);
        var staged = Path.Combine(Path.GetDirectoryName(plan.Target)!, ".krosforge-update-" + Path.GetFileName(directory) + ".new");
        var backup = BackupPath(plan.Target, directory);
        if (File.Exists(staged) || File.Exists(backup)) throw new IOException("An update is already staged.");
        try
        {
            await using (var source = File.OpenRead(Path.Combine(directory, "Krosforge.exe")))
            await using (var destination = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                await source.CopyToAsync(destination, token);
                await destination.FlushAsync(token);
            }
            if (!string.Equals(await ReleaseClient.HashAsync(staged, token), plan.CandidateHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Staged executable checksum mismatch.");
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!string.Equals(await ReleaseClient.HashAsync(plan.Target, token), plan.CurrentHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The installed executable changed.");
                try { File.Replace(staged, plan.Target, backup); break; }
                catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(200, token); }
            }
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
    }
    public static async Task<bool> CompleteRestartAsync(string[] args)
    {
        var index = Array.IndexOf(args, "--cleanup-update");
        if (index < 0) return true;
        if (index + 1 >= args.Length) return false;
        try
        {
            var directory = ValidateDirectory(args[index + 1]);
            var plan = JsonSerializer.Deserialize<UpdatePlan>(await File.ReadAllTextAsync(Path.Combine(directory, "update.json")));
            if (plan is null || !UpdatePaths.Same(plan.Target, Environment.ProcessPath!)) return false;
            var actual = await ReleaseClient.HashAsync(plan.Target, CancellationToken.None);
            if (actual != plan.CandidateHash && actual != plan.CurrentHash) return false;
            await File.WriteAllTextAsync(Path.Combine(directory, "started"), "");
            // Do not remove the acknowledgement while the helper is still waiting for it.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(Path.Combine(directory, "completed")))
            {
                if (DateTime.UtcNow >= deadline) return true;
                await Task.Delay(100);
            }
            for (var attempt = 0; attempt < 60; attempt++)
            {
                try { DeleteStaging(directory); return true; }
                catch (IOException) { await Task.Delay(200); }
                catch (UnauthorizedAccessException) { await Task.Delay(200); }
            }
            return true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException) { return false; }
    }
    public static void DeleteStaging(string directory)
    {
        directory = ValidateDirectory(directory);
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) throw new IOException("Unexpected update staging contents.");
        }
        Directory.Delete(directory, true);
    }
}
