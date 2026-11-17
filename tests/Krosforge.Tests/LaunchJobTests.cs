using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed class LaunchJobTests
{
    [Fact]
    public void AcceptedCancellationPreventsStartingTheProcess()
    {
        var job = new ClientLaunchManager.LaunchJob(Guid.NewGuid());
        using var cancellation = job.Cancellation;
        Assert.True(job.CancelPreparation());
        Assert.Throws<OperationCanceledException>(job.BeginStarting);
        Assert.Null(job.Status.ProcessId);
    }
    [Fact]
    public void StartingProcessCannotReportSuccessfulPreparationCancellation()
    {
        var job = new ClientLaunchManager.LaunchJob(Guid.NewGuid());
        using var cancellation = job.Cancellation;
        job.BeginStarting();
        Assert.False(job.CancelPreparation());
        Assert.False(cancellation.IsCancellationRequested);
    }
    [Theory]
    [InlineData("authenticating")]
    [InlineData("server-selection")]
    public void RecordingProcessDoesNotOverwriteAnAdvancedNativeStatus(string stage)
    {
        var job = new ClientLaunchManager.LaunchJob(Guid.NewGuid());
        using var cancellation = job.Cancellation;
        job.BeginStarting();
        job.Update(stage);
        job.RecordProcess(123);
        Assert.Equal(stage, job.Status.Stage);
        Assert.Equal(123, job.Status.ProcessId);
    }
    [Fact]
    public async Task CancellationAndProcessStartCannotBothSucceed()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = new ClientLaunchManager.LaunchJob(Guid.NewGuid());
            using var cancellation = job.Cancellation;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancel = Task.Run(async () => { await ready.Task; return job.CancelPreparation(); }, TestContext.Current.CancellationToken);
            var start = Task.Run(async () =>
            {
                await ready.Task;
                try { job.BeginStarting(); return true; }
                catch (OperationCanceledException) { return false; }
            }, TestContext.Current.CancellationToken);
            ready.SetResult();
            await Task.WhenAll(cancel, start);
            Assert.NotEqual(await cancel, await start);
        }
    }
}
