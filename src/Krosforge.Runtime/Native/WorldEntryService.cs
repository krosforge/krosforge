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
    internal sealed class Motion(WorldPath path, int[] turns, long started)
    {
        internal WorldPath Path { get; } = path;
        internal int[] Turns { get; } = turns;
        internal long Started { get; } = started;
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
            var selection = new Selection(this, profile, connection, character, account, emit ?? (_ => { }), disconnect ?? (() => { })) { Location = location };
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
                if (other.Moving is { } motion)
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
            var motion = new Motion(path, request.TurningCells.ToArray(), clock.GetTimestamp());
            selection.Moving = motion;
            Broadcast(selection.Location.MapId, new ActorMoved(selection.Character.Id, path.Cells));
            selection.MovementCompletion = CompleteLater(selection, motion);
        }
    }
    private async Task CompleteLater(Selection selection, Motion motion)
    {
        try
        {
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
        selection.Emit(new MovementReleased());
    }
    internal void Cancel(Selection selection, int cell)
    {
        lock (gate)
        {
            Require(selection);
            if (!selection.MapReady) throw new InvalidDataException("Map is not ready.");
            CompleteExpiredMotion(selection);
            if (selection.Moving is not { } motion || !motion.Path.CanCancel(cell, Elapsed(motion))) { Reject(selection); return; }
            var index = Array.IndexOf(motion.Path.Cells.ToArray(), cell);
            var facing = index == 0 ? selection.Location.Direction : selection.Map.Movement!.Facing(motion.Path.Cells[index - 1], cell);
            if (cell != selection.Location.CellId || facing != selection.Location.Direction)
                Persist(selection, selection.Location with { CellId = cell, Direction = facing, Revision = checked(selection.Location.Revision + 1) });
            StopMotion(selection);
            Broadcast(selection.Location.MapId, new ActorCorrected(selection.Character.Id, selection.Location.CellId));
            selection.Emit(new MovementReleased());
        }
    }
    internal void ChangeMap(Selection selection, long destination)
    {
        lock (gate)
        {
            Require(selection);
            if (!selection.MapReady) throw new InvalidDataException("Map is not ready.");
            CompleteExpiredMotion(selection);
            var next = selection.Moving is null ? catalog.Transition(selection.Location, destination) : null;
            if (next is null) { Reject(selection); return; }
            var previousMap = selection.Location.MapId;
            Persist(selection, next);
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
    private void Reject(Selection selection)
    {
        StopMotion(selection);
        Broadcast(selection.Location.MapId, new ActorCorrected(selection.Character.Id, selection.Location.CellId));
        selection.Emit(new MovementReleased());
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
