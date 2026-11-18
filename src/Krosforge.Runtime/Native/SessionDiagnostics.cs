using System.Text;
using System.Threading.Channels;
namespace Krosforge.Runtime.Native;

// A bounded recent history, written outside the movement coordinator. Rotating
// files keep later reproductions available without growing indefinitely.
internal sealed class SessionDiagnostics : IDisposable
{
    private const int MaximumFileBytes = 1024 * 1024;
    private const int FileCount = 3;
    private readonly Channel<string> messages;
    private readonly Task writer;
    private long dropped;
    internal SessionDiagnostics(string dataRoot)
    {
        messages = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest }, _ => Interlocked.Increment(ref dropped));
        writer = Task.Run(() => Drain(dataRoot));
    }
    internal void Write(string message) => messages.Writer.TryWrite($"{DateTimeOffset.UtcNow:O} {message}");
    private async Task Drain(string root)
    {
        var directory = Path.Combine(root, "logs");
        var prefix = "session-" + Guid.NewGuid().ToString("N");
        StreamWriter? output = null;
        var index = 0; var bytes = 0;
        try
        {
            while (await messages.Reader.WaitToReadAsync())
            {
                while (messages.Reader.TryRead(out var line))
                {
                    var missed = Interlocked.Exchange(ref dropped, 0);
                    if (missed > 0) line = $"{DateTimeOffset.UtcNow:O} diagnostics-dropped count={missed}\n" + line;
                    var size = Encoding.UTF8.GetByteCount(line) + 1;
                    if (output is null || bytes + size > MaximumFileBytes)
                    {
                        if (output is not null) await output.DisposeAsync();
                        output = null;
                        SafePaths.CheckTree(directory); Directory.CreateDirectory(directory);
                        var path = Path.Combine(directory, $"{prefix}-{index}.log");
                        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return;
                        output = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { NewLine = "\n" };
                        index = (index + 1) % FileCount; bytes = 0;
                    }
                    await output.WriteLineAsync(line); bytes += size;
                }
                if (output is not null) await output.FlushAsync();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        finally
        {
            messages.Writer.TryComplete();
            if (output is not null)
                try { await output.DisposeAsync(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
    public void Dispose()
    {
        messages.Writer.TryComplete();
        writer.GetAwaiter().GetResult();
    }
}
