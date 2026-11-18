using Krosforge.Core;
using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed partial class WorldSessionTests
{
    [Fact]
    public async Task InterruptionCanReconcileTheLastUnacknowledgedPathAfterItsTimerCompleted()
    {
        using var f = new MovementFixture(deferTimers: true, cancellationLeadSteps: 1);
        var (a, _) = f.Join("Alpha");
        f.Service.Move(a, new(100, [0, 3], 0)); var old = a.Moving!;
        f.Clock.Advance(TimeSpan.FromSeconds(3)); f.Service.Advance(a, old); await a.MovementCompletion;
        Assert.Equal(3, a.Location.CellId);
        f.Service.Cancel(a, 2); // Interruption was sent while animation was still in progress.
        Assert.Equal(new WorldLocation(100, 2, 0, 3), a.Location);
        f.Service.Move(a, new(100, [2, 0], 4)); var next = a.Moving!;
        f.Service.Acknowledge(a); f.Service.Advance(a, old);
        Assert.Same(next, a.Moving);
        f.Clock.Advance(TimeSpan.FromSeconds(2)); f.Service.Advance(a, next); await a.MovementCompletion;
        f.Service.Acknowledge(a); f.Service.Cancel(a, 1);
        Assert.Equal(new WorldLocation(100, 0, 4, 4), a.Location);
    }

    [Fact]
    public async Task ANewActionInvalidatesTheCompletedPathsInterruptionWindow()
    {
        using var f = new MovementFixture(deferTimers: true, cancellationLeadSteps: 1);
        var (a, _) = f.Join("Alpha");
        f.Service.Move(a, new(100, [0, 3], 0)); f.Clock.Advance(TimeSpan.FromSeconds(3));
        f.Service.Move(a, new(100, [3, 500], 0)); // Finishes the elapsed path, then rejects the next request.
        await a.MovementCompletion;
        f.Service.Cancel(a, 1);
        Assert.Equal(new WorldLocation(100, 3, 0, 2), a.Location);
    }

    [Fact]
    public async Task ReversingInsideTheActiveSegmentRetracesOnlyTheTravelledFraction()
    {
        using var f = new MovementFixture(deferTimers: true, cancellationLeadSteps: 1);
        var (a, _) = f.Join("Alpha");
        f.Service.Move(a, new(100, [0, 3], 0));
        f.Clock.Advance(TimeSpan.FromMilliseconds(600)); f.Service.Cancel(a, 1);
        f.Service.Move(a, new(100, [1, 0], 4)); var reverse = a.Moving!;
        Assert.True(reverse.Announced); Assert.Equal(TimeSpan.FromMilliseconds(600), reverse.Path.Duration);
        f.Clock.Advance(TimeSpan.FromMilliseconds(300)); f.Service.Cancel(a, 0);
        f.Service.Move(a, new(100, [0, 2], 0)); var forward = a.Moving!;
        Assert.Equal(TimeSpan.FromMilliseconds(1700), forward.Path.Duration);
        f.Service.Advance(a, reverse); Assert.Same(forward, a.Moving);
        f.Clock.Advance(TimeSpan.FromMilliseconds(1699)); f.Service.Advance(a, forward); Assert.Equal(0, a.Location.CellId);
        f.Clock.Advance(TimeSpan.FromMilliseconds(1)); f.Service.Advance(a, forward); await a.MovementCompletion;
        Assert.Equal(new WorldLocation(100, 2, 0, 4), a.Location);
    }

    [Fact]
    public void CancellingOrRejectingAnUnfinishedReversalDoesNotEraseItsPartialSegment()
    {
        using var f = new MovementFixture(deferTimers: true, cancellationLeadSteps: 1);
        var (a, _) = f.Join("Alpha");
        f.Service.Move(a, new(100, [0, 3], 0)); f.Clock.Advance(TimeSpan.FromMilliseconds(200)); f.Service.Cancel(a, 1);
        f.Service.Move(a, new(100, [1, 0], 4)); f.Clock.Advance(TimeSpan.FromMilliseconds(100));
        f.Service.Move(a, new(100, [500, 2], 0)); // Reject while the visual position is still between 0 and 1.
        for (var i = 0; i < 10; i++)
        {
            f.Service.Move(a, new(100, [1, 2], 0)); f.Service.Cancel(a, 2);
            Assert.Equal(1, a.Location.CellId);
        }
        f.Service.Move(a, new(100, [1, 0], 4));
        Assert.Equal(TimeSpan.FromMilliseconds(100), a.Moving!.Path.Duration);
    }

    [Fact]
    public async Task RapidRedirectKeepsTheActiveSegmentAndCarriesItsRemainingTime()
    {
        using var f = new MovementFixture(deferTimers: true, cancellationLeadSteps: 1);
        var (a, notices) = f.Join("Alpha"); var (_, observer) = f.Join("Beta");
        f.Service.Move(a, new(100, [0, 3], 0)); var old = a.Moving!; var pending = a.MovementCompletion;
        f.Clock.Advance(TimeSpan.FromMilliseconds(600));
        f.Service.Cancel(a, 1); // Native client reports the active segment's destination.
        Assert.Equal(new WorldLocation(100, 1, 0, 2), a.Location);
        Assert.Equal(1, observer.OfType<ActorCorrected>().Last().Cell);
        Assert.IsType<MovementReleased>(notices[^1]);
        Assert.Empty(notices.OfType<ActorCorrected>()); // Preserve the owner's interpolation.
        f.Service.Move(a, new(100, [1, 3], 0)); var redirected = a.Moving!;
        Assert.Equal(2, observer.OfType<ActorMoved>().Count()); // Echo now, before another predicted destination.
        Assert.Equal(new[] { 1, 2, 3 }, Assert.IsType<ActorMoved>(notices[^1]).Cells);
        await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        f.Service.Advance(a, old); Assert.Same(redirected, a.Moving);
        f.Clock.Advance(TimeSpan.FromMilliseconds(2399)); f.Service.Advance(a, redirected);
        Assert.Equal(1, a.Location.CellId); // 400 ms remaining + 2000 ms for the next two segments.
        Assert.Equal(new[] { 1, 2, 3 }, observer.OfType<ActorMoved>().Last().Cells);
        f.Clock.Advance(TimeSpan.FromMilliseconds(1)); f.Service.Advance(a, redirected);
        await a.MovementCompletion;
        Assert.Equal(new WorldLocation(100, 3, 0, 3), a.Location);
        Assert.Equal(a.Location, f.Locations.LoadOrCreate(a.Profile, 290, a.Character.Id, f.Catalog.Initial, f.Catalog.Validate));
    }

    [Fact]
    public void RepeatedCancelsCannotAccumulateLeadCellsOrSkipTimeOnMapExit()
    {
        using var f = new MovementFixture(deferTimers: true, cancellationLeadSteps: 1);
        var (a, _) = f.Join("Alpha");
        f.Service.Move(a, new(100, [0, 3], 0));
        f.Clock.Advance(TimeSpan.FromMilliseconds(1001)); f.Service.Cancel(a, 2);
        Assert.Equal(2, a.Location.CellId);
        f.Service.ChangeMap(a, 200); Assert.Equal(100, a.Location.MapId);
        for (var i = 0; i < 10; i++)
        {
            f.Service.Move(a, new(100, [2, 3], 0)); f.Service.Cancel(a, 3);
            Assert.Equal(2, a.Location.CellId); Assert.Null(a.Moving);
        }
        Assert.Equal(2, a.Location.Revision);
        f.Clock.Advance(TimeSpan.FromMilliseconds(999)); f.Service.ChangeMap(a, 200);
        Assert.Equal(new WorldLocation(200, 0, 0, 3), a.Location);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("cancel")]
    [InlineData("transition")]
    public void InvalidRequestDuringMovementStopsAtReachedCellInsteadOfRewinding(string action)
    {
        using var f = new MovementFixture(deferTimers: true, cancellationLeadSteps: 1);
        var (a, notices) = f.Join("Alpha"); var (_, observer) = f.Join("Beta");
        f.Service.Move(a, new(100, [0, 3], 0)); f.Clock.Advance(TimeSpan.FromMilliseconds(1100));
        switch (action)
        {
            case "move": f.Service.Move(a, new(100, [3, 0], 4)); break;
            case "cancel": f.Service.Cancel(a, 3); break; // Beyond the current segment.
            case "transition": f.Service.ChangeMap(a, 200); break;
        }
        Assert.Null(a.Moving); Assert.Equal(new WorldLocation(100, 1, 0, 2), a.Location);
        Assert.Equal(1, Assert.Single(observer.OfType<ActorCorrected>()).Cell);
        Assert.IsType<MovementReleased>(notices[^2]);
        Assert.Equal(a.Location, f.Locations.LoadOrCreate(a.Profile, 290, a.Character.Id, f.Catalog.Initial, f.Catalog.Validate));
    }
}
