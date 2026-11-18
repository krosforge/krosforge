using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed class WorldOutputTests
{
    private sealed class BlockedStream : MemoryStream
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
    }
    [Fact]
    public async Task SlowPeerOverflowDoesNotBlockAnotherOutput()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var blocked = new BlockedStream();
        await using var slow = new WorldOutput(blocked, timeout.Token, capacity: 1);
        slow.Post([1]); await blocked.Started.Task.WaitAsync(timeout.Token);
        slow.Post([2]); slow.Post([3]); Assert.True(slow.Token.IsCancellationRequested);
        using var memory = new MemoryStream();
        await using var fast = new WorldOutput(memory, timeout.Token);
        await fast.Send([4, 5], timeout.Token);
        Assert.Equal(new byte[] { 2, 4, 5 }, memory.ToArray());
    }
    [Fact]
    public async Task ConcurrentProducersNeverInterleaveFramesAndPreserveEachProducerOrder()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var memory = new MemoryStream(); await using var output = new WorldOutput(memory, timeout.Token, capacity: 256);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(producer => Task.Run(() =>
        {
            for (var i = 0; i < 32; i++) output.Post([(byte)producer, (byte)i]);
        }, timeout.Token)));
        await output.Send(null, timeout.Token);
        var bytes = memory.ToArray(); Assert.Equal(128 * 3, bytes.Length);
        int[] next = [0, 0, 0, 0];
        for (var i = 0; i < bytes.Length; i += 3)
        { Assert.Equal(2, bytes[i]); Assert.Equal(next[bytes[i + 1]]++, bytes[i + 2]); }
        Assert.All(next, count => Assert.Equal(32, count));
    }
}
