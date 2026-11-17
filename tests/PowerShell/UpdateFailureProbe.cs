using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

// Test-only candidate: exits without acknowledging startup while a separate process
// holds a read handle that denies replacement of the installed executable for 3 seconds.
internal static class UpdateFailureProbe
{
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--hold-lock")
        {
            using (var held = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                File.WriteAllText(args[2], "locked");
                Thread.Sleep(3000);
            }
            return 0;
        }
        var index = Array.IndexOf(args, "--cleanup-update");
        if (index < 0 || index + 1 >= args.Length) return 70;
        var stage = args[index + 1];
        var marker = Path.Combine(stage, "rollback-read-lock");
        var target = Assembly.GetExecutingAssembly().Location;
        using (var worker = Process.Start(new ProcessStartInfo(Path.Combine(stage, "RollbackLockProbe.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            Arguments = "--hold-lock \"" + target + "\" \"" + marker + "\""
        }))
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(marker))
            {
                if (worker.HasExited || DateTime.UtcNow >= deadline) return 71;
                Thread.Sleep(10);
            }
        }
        return 17;
    }
}
