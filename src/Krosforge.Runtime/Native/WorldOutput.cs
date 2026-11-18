using System.Threading.Channels;
namespace Krosforge.Runtime.Native;

// One bounded writer per socket keeps snapshots and presence events ordered.
// A slow peer is disconnected instead of blocking other profiles or growing a queue.
internal sealed class WorldOutput : IAsyncDisposable
{
    private sealed record Pending(byte[]? Payload, TaskCompletionSource? Completion);
    private readonly Stream stream;
    private readonly Channel<Pending> queue;
    private readonly CancellationTokenSource lifetime;
    private readonly Task writer;
    private readonly TimeSpan writeTimeout;
    internal CancellationToken Token => lifetime.Token;
    internal WorldOutput(Stream stream, CancellationToken token, int capacity = 128, TimeSpan? writeTimeout = null)
    {
        this.stream = stream; this.writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(10);
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(capacity)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
        writer = Run();
    }
    internal void Post(byte[] payload)
    {
        if (!queue.Writer.TryWrite(new(payload, null))) Abort();
    }
    internal Task Send(byte[]? payload, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.Writer.TryWrite(new(payload, completion))) { Abort(); throw new IOException("World output queue unavailable."); }
        return completion.Task.WaitAsync(token);
    }
    internal void Abort()
    {
        lifetime.Cancel(); queue.Writer.TryComplete(); stream.Dispose();
    }
    private async Task Run()
    {
        Pending? current = null;
        try
        {
            await foreach (var item in queue.Reader.ReadAllAsync(lifetime.Token))
            {
                current = item;
                if (item.Payload is not null)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    deadline.CancelAfter(writeTimeout);
                    await WorldProtocol.Write(stream, item.Payload, deadline.Token);
                }
                item.Completion?.TrySetResult(); current = null;
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            current?.Completion?.TrySetException(new IOException("World output ended.", error));
            Abort();
        }
        finally
        {
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out var item)) item.Completion?.TrySetException(new IOException("World output ended."));
        }
    }
    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete(); lifetime.Cancel(); await writer; lifetime.Dispose();
    }
}
