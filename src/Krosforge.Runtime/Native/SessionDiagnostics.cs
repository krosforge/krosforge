namespace Krosforge.Runtime.Native;

internal sealed class SessionDiagnostics(string dataRoot)
{
    private readonly object gate = new();
    private readonly string filename = "session-" + Guid.NewGuid().ToString("N") + ".log";
    private int remaining = 256;
    internal void Write(string message)
    {
        lock (gate)
        {
            if (remaining-- <= 0) return;
            try
            {
                var directory = Path.Combine(dataRoot, "logs");
                SafePaths.CheckTree(directory); Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, filename);
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}\n");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
