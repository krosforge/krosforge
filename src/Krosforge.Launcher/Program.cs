using Avalonia;
using Krosforge.Contracts;
namespace Krosforge.Launcher;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--apply-update")
        {
            Environment.ExitCode = Updates.UpdateInstaller.RunHelperAsync(args[1]).GetAwaiter().GetResult();
            return;
        }
        if (args.Length > 0 && args[0] == "--runtime")
        {
            Environment.ExitCode = Krosforge.Runtime.RuntimeEntry.RunAsync(args[1..]).GetAwaiter().GetResult();
            return;
        }
        using var mutex = new Mutex(true, "Krosforge.Launcher." + AppPaths.Pipe(AppPaths.Data), out var owner);
        if (!owner)
            return;
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
        }
        finally { mutex.ReleaseMutex(); }
    }
}
