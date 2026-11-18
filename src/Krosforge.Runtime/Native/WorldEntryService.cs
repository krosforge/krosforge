using Krosforge.Core;
using Microsoft.Data.Sqlite;
namespace Krosforge.Runtime.Native;

internal sealed class WorldEntryService(CharacterService characters, LocationStore locations, WorldEntryCatalog catalog, TimeProvider? time = null)
{
    // Local accounts share one coordinator. It only holds short state/SQLite
    // operations; socket writes happen in independent bounded output queues.
    private readonly object gate = new();
    private readonly Dictionary<long, Selection> active = [];
    private readonly WorldEntryCatalog catalog = catalog;
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    internal sealed class Motion(WorldPath path, int[] turns, long started, TimeSpan firstStepCredit)
    {
        internal WorldPath Path { get; } = path;
        internal int[] Turns { get; } = turns;
        internal long Started { get; } = started;
        internal bool Announced { get; set; }
        internal TimeSpan FirstStepCredit { get; } = firstStepCredit;
        internal CancellationTokenSource Cancellation { get; } = new();
    }
    internal sealed class Selection(WorldEntryService owner, Guid profile, object connection, CharacterWire character, long account,
        Action<WorldNotice> emit, Action disconnect) : IDisposable
    {
        internal Guid Profile { get; } = profile;
        internal object Connection { get; } = connection;
        internal CharacterWire Character { get; } = character;
        internal long Account { get; } = account;
        internal WorldLocation Location { get; set; } = null!;
        internal EntryAttributes Attributes => owner.catalog.Attributes;
        internal EntryMap Map => owner.catalog.Map(Location.MapId);
        internal bool ContextCreated { get; set; }
        internal bool MapReady { get; set; }
        internal Action<WorldNotice> Emit { get; } = emit;
        internal Action Disconnect { get; } = disconnect;
        internal Motion? Moving { get; set; }
        internal Motion? UnacknowledgedMotion { get; set; }
        internal long MovementAvailableAt { get; set; }
        internal int? InterruptedFrom { get; set; }
        internal Task MovementCompletion { get; set; } = Task.CompletedTask;
        public void Dispose() => owner.Release(this);
    }
    internal Selection? Select(Guid profile, long account, object connection, long id, Action<WorldNotice>? emit = null, Action? disconnect = null)
    {
        lock (gate)
        {
            var character = characters.List(profile).SingleOrDefault(c => c.Id == id);
            if (character is null || active.ContainsKey(id)) return null;
            var location = locations.LoadOrCreate(profile, ServerPresentation.RealmId, id, catalog.Initial, catalog.Validate);
            if (location is null) return null;
            var selection = new Selection(this, profile, connection, character, account, emit ?? (_ => { }), disconnect ?? (() => { }))
            {
                Location = location,
                MovementAvailableAt = EarliestPredictionStart()
            };
            active.Add(id, selection); return selection;
        }
    }
    private bool Current(Selection selection) => active.TryGetValue(selection.Character.Id, out var current) && ReferenceEquals(current, selection);
    private void Require(Selection selection)
    {
        if (!Current(selection)) throw new InvalidDataException("World selection expired.");
    }
    internal void Join(Selection selection, long map)
    {
        lock (gate)
        {
            Require(selection);
            if (!selection.ContextCreated || selection.Location.MapId != map) throw new InvalidDataException("Map load does not match selection.");
            var first = !selection.MapReady;
            selection.MapReady = true;
            var now = clock.GetTimestamp();
            var visible = Members(map).OrderBy(s => s.Character.Id).ToArray();
            selection.Emit(new MapSnapshot(selection.Map, visible.Select(s => Snapshot(s, now)).ToArray()));
            foreach (var other in visible)
                if (other.Moving is { Announced: true } motion)
                {
                    var index = motion.Path.ReachedIndex(clock.GetElapsedTime(motion.Started, now));
                    var remaining = motion.Path.Cells.Skip(index).ToArray();
                    if (remaining.Length > 1) selection.Emit(new ActorMoved(other.Character.Id, remaining));
                }
            if (first) Broadcast(map, new ActorAppeared(Snapshot(selection, now)), except: selection);
        }
    }
    internal void Move(Selection selection, MovementRequest request)
    {
        lock (gate)
        {
            Require(selection);
            if (!selection.MapReady) throw new InvalidDataException("Map is not ready.");
            CompleteExpiredMotion(selection);
            if (selection.Moving is { } previous && request.Map == selection.Location.MapId && request.TurningCells.SequenceEqual(previous.Turns)) return;
            var path = request.Map == selection.Location.MapId
                ? selection.Map.Movement!.Validate(selection.Location.CellId, request.TurningCells, request.Direction, catalog.Movement) : null;
            if (selection.Moving is not null || path is null) { Reject(selection); return; }
            var now = clock.GetTimestamp();
            var credit = selection.InterruptedFrom == path.Cells[1] && selection.MovementAvailableAt > now
                ? clock.GetElapsedTime(now, selection.MovementAvailableAt) : TimeSpan.Zero;
            // Turning back starts at the interpolated position inside the old segment.
            // It retraces only the travelled portion, not an entire additional cell.
            if (credit > TimeSpan.Zero) path = path.ReverseFromPartialSegment(credit);
            // The bounded prediction allowance is shared with the previous deadline.
            // Completed paths cannot earn another allowance by sending a new request.
            var started = credit > TimeSpan.Zero ? now : Math.Max(EarliestPredictionStart(), selection.MovementAvailableAt);
            var motion = new Motion(path, request.TurningCells.ToArray(), started, credit);
            selection.Moving = motion;
            selection.UnacknowledgedMotion = motion;
            AnnounceMovement(selection, motion);
            selection.MovementCompletion = CompleteLater(selection, motion);
        }
    }
    private void AnnounceMovement(Selection selection, Motion motion)
    {
        // Native movement is predicted before the reply. Delaying its echo until
        // the durable timeline catches up can overwrite a newer predicted path.
        if (motion.Announced) return;
        motion.Announced = true;
        Broadcast(selection.Location.MapId, new ActorMoved(selection.Character.Id, motion.Path.Cells));
    }
    private async Task CompleteLater(Selection selection, Motion motion)
    {
        try
        {
            while (true)
            {
                var untilStart = -Elapsed(motion);
                if (untilStart <= TimeSpan.Zero) break;
                await Task.Delay(untilStart, clock, motion.Cancellation.Token);
            }
            lock (gate)
            {
                if (!Current(selection) || !ReferenceEquals(selection.Moving, motion)) return;
                AnnounceMovement(selection, motion);
            }
            while (true)
            {
                var remaining = motion.Path.Duration - Elapsed(motion);
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining, clock, motion.Cancellation.Token);
            }
            Advance(selection, motion);
        }
        catch (OperationCanceledException) when (motion.Cancellation.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or SqliteException or OverflowException)
        {
            // No in-memory destination is published when its durable update fails.
            selection.Dispose(); selection.Disconnect();
        }
        finally { motion.Cancellation.Dispose(); }
    }
    internal void Advance(Selection selection, Motion motion)
    {
        lock (gate)
        {
            if (!Current(selection) || !ReferenceEquals(selection.Moving, motion)) return;
            AnnounceMovement(selection, motion);
            CompleteExpiredMotion(selection);
        }
    }
    // Called under gate by both the timer and incoming actions. Scheduling delay
    // must not turn an elapsed movement into a rejection or a partial cancellation.
    private void CompleteExpiredMotion(Selection selection)
    {
        if (selection.Moving is not { } motion || Elapsed(motion) < motion.Path.Duration) return;
        Persist(selection, selection.Location with { CellId = motion.Path.Cells[^1], Direction = motion.Path.Direction, Revision = checked(selection.Location.Revision + 1) });
        StopMotion(selection);
        selection.InterruptedFrom = null;
        selection.MovementAvailableAt = checked(motion.Started + TimestampTicks(motion.Path.Duration));
        selection.Emit(new MovementReleased());
    }
    internal void Cancel(Selection selection, int cell)
    {
        lock (gate)
        {
            Require(selection);
            if (!selection.MapReady) throw new InvalidDataException("Map is not ready.");
            CompleteExpiredMotion(selection);
            var motion = selection.Moving ?? selection.UnacknowledgedMotion;
            if (motion is null || !motion.Path.CanCancel(cell, Elapsed(motion))) { Reject(selection); return; }
            var index = Array.IndexOf(motion.Path.Cells.ToArray(), cell);
            PersistCell(selection, motion.Path, index);
            RetainPartialSegment(selection, motion, index);
            StopMotion(selection);
            selection.UnacknowledgedMotion = null;
            selection.Emit(new MovementReleased());
            // The owner already interrupted at this admitted cell. Repositioning
            // it again disrupts its interpolated path and the next prediction.
            Broadcast(selection.Location.MapId, new ActorCorrected(selection.Character.Id, selection.Location.CellId), except: selection);
        }
    }
    internal void Acknowledge(Selection selection)
    {
        lock (gate)
        {
            Require(selection);
            // ACK has no movement identity. Never finish a path or clear a newer
            // active movement because an earlier animation's ACK arrived late.
            if (selection.Moving is null) selection.UnacknowledgedMotion = null;
        }
    }
    internal void ChangeMap(Selection selection, long destination)
    {
        lock (gate)
        {
            Require(selection);
            if (!selection.MapReady) throw new InvalidDataException("Map is not ready.");
            CompleteExpiredMotion(selection);
            var next = selection.Moving is null && clock.GetTimestamp() >= selection.MovementAvailableAt
                ? catalog.Transition(selection.Location, destination) : null;
            if (next is null) { Reject(selection); return; }
            var previousMap = selection.Location.MapId;
            Persist(selection, next);
            selection.UnacknowledgedMotion = null;
            selection.MapReady = false;
            Broadcast(previousMap, new ActorDeparted(selection.Character.Id));
            selection.Emit(new MapLoading(next.MapId));
        }
    }
    private void Persist(Selection selection, WorldLocation next)
    {
        locations.Save(selection.Profile, ServerPresentation.RealmId, selection.Character.Id, selection.Location, next, catalog.Validate);
        selection.Location = next;
    }
    private void PersistCell(Selection selection, WorldPath path, int index)
    {
        var cell = path.Cells[index];
        var facing = index == 0 ? selection.Location.Direction : selection.Map.Movement!.Facing(path.Cells[index - 1], cell);
        if (cell != selection.Location.CellId || facing != selection.Location.Direction)
            Persist(selection, selection.Location with { CellId = cell, Direction = facing, Revision = checked(selection.Location.Revision + 1) });
    }
    private void RetainPartialSegment(Selection selection, Motion motion, int index)
    {
        var now = clock.GetTimestamp();
        var elapsed = clock.GetElapsedTime(motion.Started, now);
        if (index == 0 && elapsed < TimeSpan.Zero) return; // Waiting for the prior segment.
        if (index == 0 && motion.FirstStepCredit > TimeSpan.Zero && motion.Path.ReachedIndex(elapsed) == 0)
        {
            // A rejected/cancelled reversal must not erase the outstanding fraction
            // to its logical origin, otherwise alternating bad requests could mint steps.
            selection.InterruptedFrom = motion.Path.Cells[1];
            selection.MovementAvailableAt = checked(now + TimestampTicks(motion.FirstStepCredit + elapsed));
            return;
        }
        var arrival = checked(motion.Started + TimestampTicks(motion.Path.ArrivalAt(index)));
        selection.InterruptedFrom = index > 0 && arrival > now ? motion.Path.Cells[index - 1] : null;
        selection.MovementAvailableAt = Math.Max(now, arrival);
    }
    private long EarliestPredictionStart() => checked(clock.GetTimestamp() - TimestampTicks(TimeSpan.FromMilliseconds(catalog.Movement.PredictionLeadMilliseconds)));
    private long TimestampTicks(TimeSpan duration) => checked((long)Math.Ceiling(duration.TotalSeconds * clock.TimestampFrequency));
    private void Reject(Selection selection)
    {
        // A bad request stops at the reached cell, never at the old path's origin.
        if (selection.Moving is { } motion)
        {
            var index = motion.Path.ReachedIndex(Elapsed(motion));
            PersistCell(selection, motion.Path, index);
            RetainPartialSegment(selection, motion, index);
        }
        StopMotion(selection);
        selection.UnacknowledgedMotion = null;
        selection.Emit(new MovementReleased());
        Broadcast(selection.Location.MapId, new ActorCorrected(selection.Character.Id, selection.Location.CellId));
    }
    private static void StopMotion(Selection selection)
    {
        var previous = selection.Moving; selection.Moving = null; previous?.Cancellation.Cancel();
    }
    private TimeSpan Elapsed(Motion motion) => clock.GetElapsedTime(motion.Started, clock.GetTimestamp());
    private WorldActor Snapshot(Selection selection, long now)
    {
        var position = selection.Location;
        if (selection.Moving is { } motion) position = position with { CellId = motion.Path.Cells[motion.Path.ReachedIndex(clock.GetElapsedTime(motion.Started, now))] };
        return new(selection.Character, selection.Account, position);
    }
    private IEnumerable<Selection> Members(long map) => active.Values.Where(s => s.MapReady && s.Location.MapId == map);
    private void Broadcast(long map, WorldNotice notice, Selection? except = null)
    {
        foreach (var member in Members(map)) if (!ReferenceEquals(member, except)) member.Emit(notice);
    }
    private void Release(Selection selection)
    {
        lock (gate)
        {
            if (!Current(selection)) return;
            StopMotion(selection); active.Remove(selection.Character.Id);
            if (selection.MapReady) Broadcast(selection.Location.MapId, new ActorDeparted(selection.Character.Id));
            selection.MapReady = false;
        }
    }
}
