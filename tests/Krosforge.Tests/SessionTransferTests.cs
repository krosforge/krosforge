using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;
public sealed class SessionTransferTests
{
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public void ReturnPreservesIdentityAndOldCleanupCannotReleaseNewSession()
    {
        var state = new SessionTransfer(TimeProvider.System);
        var lease = state.Open();
        Assert.NotEqual(0, lease); Assert.Equal(0, state.Open());
        var ticket = state.Handoff(lease)!;
        Assert.Equal(0, state.Attach(ticket, true));
        Assert.Equal(lease, state.Attach(ticket, false));
        Assert.Equal(0, state.Attach(ticket, false));
        var returned = state.Return(lease)!.Value;
        state.Release(lease);
        Assert.Equal(returned.Lease, state.Attach(returned.Ticket, true));
        Assert.True(state.Current(returned.Lease, "connection"));
    }
    [Fact]
    public async Task ConcurrentConsumersHaveOnlyOneWinner()
    {
        var state = new SessionTransfer(TimeProvider.System);
        var ticket = state.Handoff(state.Open())!;
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => state.Attach(ticket, false))));
        Assert.Single(results, x => x != 0);
    }
    [Fact]
    public void ExpiredAndRevokedTicketsCannotAttach()
    {
        var clock = new Clock(); var state = new SessionTransfer(clock);
        var ticket = state.Handoff(state.Open())!;
        clock.Now += TimeSpan.FromSeconds(30);
        Assert.Equal(0, state.Attach(ticket, false));
        ticket = state.Handoff(state.Open())!;
        state.Dispose();
        Assert.Equal(0, state.Attach(ticket, false)); Assert.Equal(0, state.Open());
    }
    [Theory]
    [InlineData("connecting-server")]
    [InlineData("character-selection")]
    [InlineData("entering-world")]
    [InlineData("in-world")]
    [InlineData("returning-server")]
    [InlineData("disconnected")]
    public void LiveProcessRemainsActiveAcrossNetworkPhases(string phase)
    {
        var job = new ClientLaunchManager.LaunchJob(Guid.NewGuid());
        using var cancellation = job.Cancellation;
        job.BeginStarting(); job.Update(phase);
        Assert.True(job.Active); Assert.False(job.CancelPreparation());
        job.Update("exited"); Assert.False(job.Active);
    }
}
