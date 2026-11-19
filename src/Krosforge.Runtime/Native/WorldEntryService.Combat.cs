using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed record CombatView(long Id, CombatEncounter Encounter, CharacterWire Character,
    EntryAttributes Attributes, IReadOnlyList<CombatPlacement> Positions, IReadOnlyList<CombatSpell>? Spells = null);

internal sealed partial class WorldEntryService
{
    internal sealed class FightSession(long id, CombatEncounter encounter, long player, int initialLevel)
    {
        internal long Id { get; } = id;
        internal int InitialLevel { get; } = initialLevel;
        internal CombatEncounter Encounter { get; } = encounter;
        internal CombatPreparation Preparation { get; } = new(encounter.Board,
            new[] { new CombatPlacement(player, 0, encounter.PlayerCell, true) }
                .Concat(encounter.Opponents.Select(m => new CombatPlacement(m.Actor, 1, m.Cell, false, true))));
        internal CombatTurns? Turns { get; set; }
        internal int? Sequence { get; set; }
        internal long Deadline { get; set; }
        internal long AnimationDeadline { get; set; }
        internal long ReadyDeadline { get; set; }
        internal long AutomaticAt { get; set; }
        internal bool EndRequested { get; set; }
        internal bool AwaitingReady { get; set; }
        internal CancellationTokenSource Lifetime { get; } = new();
        internal Task Tick { get; set; } = Task.CompletedTask;
        internal bool Loaded { get; set; }
        internal bool Started { get; set; }
        internal long StartedAt { get; set; }
        internal Guid? DurableId { get; set; }
        internal CombatReceipt? Receipt { get; set; }
        internal CombatAward? Award { get; set; }
    }
    private readonly Dictionary<long, Selection> claimedGroups = [];
    private long lastFight;
    private CombatEncounter[] AvailableEncounters(long map) => combat?.Encounters
        .Where(e => e.Map == map && !claimedGroups.ContainsKey(e.GroupId)).ToArray() ?? [];
    internal void PreviewBattleMap(Selection selection)
    {
        lock (gate)
        {
            Require(selection);
            if (selection.MapReady && selection.Fight is null && selection.Map.Battle is { } battle)
                selection.Emit(new BattleMapPreview(selection.Location.MapId, battle));
        }
    }
    internal void Attack(Selection selection, long group)
    {
        lock (gate)
        {
            Require(selection);
            if (!selection.MapReady || selection.Fight is not null) return;
            CompleteExpiredMotion(selection);
            var encounter = AvailableEncounters(selection.Location.MapId).SingleOrDefault(e => e.GroupId == group);
            if (encounter is null) return;
            selection.PendingAttack = encounter;
            if (selection.Moving is not null) return;
            var route = selection.Map.Movement!.Approach(selection.Location.CellId, encounter.GroupCell, encounter.AttackDistance, catalog.Movement);
            if (route is null) { selection.PendingAttack = null; return; }
            if (route.Count > 1)
            {
                Move(selection, new(selection.Location.MapId, route.ToArray(), 0));
                selection.PendingAttack = encounter;
                if (selection.Moving is null) TryPendingAttack(selection);
            }
            else TryPendingAttack(selection);
        }
    }
    private void TryPendingAttack(Selection selection)
    {
        if (selection.PendingAttack is not { } encounter || selection.Moving is not null || !selection.MapReady) return;
        if (clock.GetTimestamp() < selection.MovementAvailableAt)
        {
            if (selection.AttackWait.IsCompleted) selection.AttackWait = WaitForAttack(selection);
            return;
        }
        selection.PendingAttack = null;
        if (selection.Location.MapId != encounter.Map || claimedGroups.ContainsKey(encounter.GroupId)) return;
        if (selection.Map.Movement!.Distance(selection.Location.CellId, encounter.GroupCell) > encounter.AttackDistance)
        {
            Attack(selection, encounter.GroupId); return;
        }
        var fight = new FightSession(checked(++lastFight), encounter, selection.Character.Id, selection.Character.Level);
        fight.DurableId = outcomes?.Begin(selection.Profile, ServerPresentation.RealmId, selection.Character.Id, encounter.GroupId, selection.Location);
        claimedGroups.Add(encounter.GroupId, selection);
        Broadcast(selection.Location.MapId, new EncounterDeparted(encounter.GroupId));
        Broadcast(selection.Location.MapId, new ActorDeparted(selection.Character.Id), except: selection);
        selection.MapReady = false;
        selection.UnacknowledgedMotion = null;
        selection.Fight = fight;
        selection.Emit(new FightEntered(fight.Id, encounter));
    }
    private async Task WaitForAttack(Selection selection)
    {
        var token = selection.Lifetime.Token;
        try
        {
            while (true)
            {
                TimeSpan remaining;
                lock (gate)
                {
                    if (!Current(selection) || selection.PendingAttack is null || !selection.MapReady || selection.Moving is not null) return;
                    remaining = clock.GetElapsedTime(clock.GetTimestamp(), selection.MovementAvailableAt);
                    if (remaining <= TimeSpan.Zero) { TryPendingAttack(selection); return; }
                }
                await Task.Delay(remaining, clock, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException or OverflowException)
        { selection.Dispose(); selection.Disconnect(); }
    }
    private void JoinFight(Selection selection, long map)
    {
        var fight = selection.Fight!;
        if (map != fight.Encounter.Battle.Id || fight.Started) throw new InvalidDataException("Fight map load does not match preparation.");
        selection.Emit(new FightLoaded(new(fight.Id, fight.Encounter, selection.Character, selection.Attributes, fight.Preparation.Fighters, spells?.Player(selection.Character.BreedId))));
        fight.Loaded = true;
    }
    internal void Place(Selection selection, long actor, int cell)
    {
        lock (gate)
        {
            Require(selection);
            if (actor != selection.Character.Id || selection.Fight is not { Loaded: true, Started: false } fight) return;
            fight.Preparation.Place(actor, cell);
            // Reply with authoritative positions even when a predicted placement is rejected.
            selection.Emit(new FightPositions(fight.Preparation.Fighters));
        }
    }
    internal void Ready(Selection selection, bool ready)
    {
        lock (gate)
        {
            Require(selection);
            if (selection.Fight is not { Loaded: true, Started: false } fight || !fight.Preparation.SetReady(selection.Character.Id, ready)) return;
            if (!fight.Preparation.Start()) return;
            fight.Started = true;
            fight.StartedAt = clock.GetTimestamp();
            if (spells is null) selection.Emit(new FightStarted(fight.Preparation.Fighters.Select(p => p.ActorId).ToArray()));
            else StartTurns(selection, fight);
        }
    }
    internal void Forfeit(Selection selection)
    {
        lock (gate)
        {
            Require(selection);
            if (selection.Fight is null) return;
            if (outcomes is not null) { FinishCombat(selection, selection.Fight, true); return; }
            ReleaseFight(selection);
            selection.MapReady = false;
            selection.MovementAvailableAt = EarliestPredictionStart();
            selection.Emit(new FightLeft(selection.Location.MapId));
        }
    }
    private void ReleaseFight(Selection selection)
    {
        if (selection.Fight is not { } fight) return;
        selection.Fight = null;
        fight.Lifetime.Cancel(); fight.Lifetime.Dispose();
        fight.Preparation.Abandon();
        if (claimedGroups.TryGetValue(fight.Encounter.GroupId, out var owner) && ReferenceEquals(owner, selection))
        {
            claimedGroups.Remove(fight.Encounter.GroupId);
            Broadcast(fight.Encounter.Map, new EncounterAppeared(fight.Encounter));
        }
    }
}
