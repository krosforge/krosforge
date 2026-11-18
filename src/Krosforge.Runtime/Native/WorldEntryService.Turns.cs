using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed record CombatTiming(int TurnMilliseconds = 30000, int AnimationMilliseconds = 15000,
    int ReadyMilliseconds = 5000, int AutomaticMilliseconds = 250);
internal sealed record FightTurn(CombatFighter Player, int Level, long NextExperience, long Actor, int Round,
    int Order, int Milliseconds, bool Confirmed) : WorldNotice;
internal sealed record FightAction(long Observer, int Sequence, CombatAction Action, CombatFighter Player, int Level, long NextExperience) : WorldNotice;
internal sealed record FightTurnEnding : WorldNotice;
internal sealed record FightActionRejected : WorldNotice;

internal sealed partial class WorldEntryService
{
    private int lastCombatSequence;
    private readonly CombatTiming combatTiming = spells?.Timing ?? new();
    private long After(int milliseconds) => checked(clock.GetTimestamp() + (long)Math.Ceiling(milliseconds / 1000d * clock.TimestampFrequency));
    private void StartTurns(Selection selection, FightSession fight)
    {
        if (spells is null) return;
        var attributes = selection.Attributes; var monster = fight.Encounter.Monster;
        fight.Turns = new(fight.Encounter.Board, fight.Preparation.Fighters.Select(position => position.Controlled
            ? new CombatFighter(position.ActorId, position.Team, true, position.Cell, attributes.Life, attributes.Life,
                attributes.ActionPoints, attributes.ActionPoints, attributes.MovementPoints, attributes.MovementPoints,
                CombatStatistics.Empty, spells.Player(selection.Character.BreedId))
            : new CombatFighter(position.ActorId, position.Team, false, position.Cell, monster.Life, monster.Life,
                monster.ActionPoints, monster.ActionPoints, monster.MovementPoints, monster.MovementPoints,
                monster.Statistics, spells.Monster(monster.Spells))));
        selection.Emit(new FightStarted(fight.Turns.Order));
        PublishTurn(selection, fight, false);
        fight.Tick = RunCombatClock(selection, fight);
    }
    private void PublishTurn(Selection selection, FightSession fight, bool confirmed)
    {
        var turns = fight.Turns!;
        fight.Deadline = After(combatTiming.TurnMilliseconds);
        fight.AutomaticAt = After(combatTiming.AutomaticMilliseconds);
        fight.EndRequested = false; fight.AwaitingReady = false;
        selection.Emit(new FightTurn(turns.Fighters.Single(f => f.Controlled), selection.Character.Level,
            selection.Attributes.NextLevelExperience, turns.Current.Id, turns.Round, turns.OrderIndex, combatTiming.TurnMilliseconds, confirmed));
    }
    private async Task RunCombatClock(Selection selection, FightSession fight)
    {
        var token = fight.Lifetime.Token;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), clock, token);
                lock (gate)
                {
                    if (!Current(selection) || !ReferenceEquals(selection.Fight, fight) || fight.Turns!.Finished && fight.Sequence is null) return;
                    AdvanceCombat(selection, fight);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException or OverflowException)
        { selection.Dispose(); selection.Disconnect(); }
    }
    internal void AdvanceCombat(Selection selection)
    {
        lock (gate) { Require(selection); if (selection.Fight is { Turns: not null } fight) AdvanceCombat(selection, fight); }
    }
    private void AdvanceCombat(Selection selection, FightSession fight)
    {
        var turns = fight.Turns!; var now = clock.GetTimestamp();
        if (fight.Sequence is not null)
        {
            // An unconfirmed animation cannot authorize a following action or another fight.
            if (now >= fight.AnimationDeadline) { selection.Dispose(); selection.Disconnect(); return; }
            if (now >= fight.Deadline) fight.EndRequested = true;
            return;
        }
        if (turns.Finished) return;
        if (fight.AwaitingReady)
        {
            if (now >= fight.ReadyDeadline) NextTurn(selection, fight);
            return;
        }
        if (now >= fight.Deadline) { EndTurn(selection, fight); return; }
        if (turns.Current.Controlled || now < fight.AutomaticAt) return;
        var actor = turns.Current;
        var opponent = turns.Fighters.Where(f => f.Alive && f.Team != actor.Team).OrderBy(f => f.Id).FirstOrDefault();
        if (opponent is null) return;
        foreach (var spell in actor.Spells)
        {
            if (!turns.CanCast(actor.Id, turns.Serial, spell.Id, opponent.Cell)) continue;
            var cast = turns.Cast(actor.Id, turns.Serial, spell.Id, opponent.Cell, Roll)!;
            PublishAction(selection, fight, cast); return;
        }
        var path = turns.Approach(actor.Id, turns.Serial, opponent.Cell);
        if (path is not null && turns.Move(actor.Id, turns.Serial, path) is { } movement)
        { PublishAction(selection, fight, movement); return; }
        EndTurn(selection, fight);
    }
    private static int Roll(int minimum, int maximum) => Random.Shared.Next(minimum, checked(maximum + 1));
    private FightSession? AvailableTurn(Selection selection)
    {
        Require(selection);
        if (selection.Fight is not { Turns: not null } fight) return null;
        AdvanceCombat(selection, fight);
        if (!Current(selection) || !ReferenceEquals(selection.Fight, fight) || fight.Sequence is not null || fight.AwaitingReady || fight.Turns.Finished ||
            fight.Turns.Current.Id != selection.Character.Id) return null;
        return fight;
    }
    internal void CombatMove(Selection selection, MovementRequest request)
    {
        lock (gate)
        {
            var fight = AvailableTurn(selection);
            if (fight is null || request.Map != selection.Location.MapId ||
                fight.Turns!.Move(selection.Character.Id, fight.Turns.Serial, request.TurningCells) is not { } action)
            { selection.Emit(new FightActionRejected()); return; }
            PublishAction(selection, fight, action);
        }
    }
    internal void Cast(Selection selection, int spell, int cell)
    {
        lock (gate)
        {
            var fight = AvailableTurn(selection);
            if (fight is null || fight.Turns!.Cast(selection.Character.Id, fight.Turns.Serial, spell, cell, Roll) is not { } action)
            { selection.Emit(new FightActionRejected()); return; }
            PublishAction(selection, fight, action);
        }
    }
    private void PublishAction(Selection selection, FightSession fight, CombatAction action)
    {
        fight.Sequence = checked(++lastCombatSequence); fight.AnimationDeadline = After(combatTiming.AnimationMilliseconds);
        selection.Emit(new FightAction(selection.Character.Id, fight.Sequence.Value, action,
            fight.Turns!.Fighters.Single(f => f.Controlled), selection.Character.Level, selection.Attributes.NextLevelExperience));
    }
    internal void CombatAcknowledge(Selection selection, int sequence)
    {
        lock (gate)
        {
            Require(selection);
            if (selection.Fight is not { Turns: not null } fight || fight.Sequence != sequence) return;
            if (clock.GetTimestamp() >= fight.AnimationDeadline) { selection.Dispose(); selection.Disconnect(); return; }
            fight.Sequence = null;
            if (fight.Turns.Finished) return; // Completion owns durable outcome and world return.
            if (fight.EndRequested || clock.GetTimestamp() >= fight.Deadline) EndTurn(selection, fight);
            else fight.AutomaticAt = After(combatTiming.AutomaticMilliseconds);
        }
    }
    internal void EndCombatTurn(Selection selection)
    {
        lock (gate)
        {
            Require(selection);
            if (selection.Fight is not { Turns: not null } fight || fight.Turns.Current.Id != selection.Character.Id || fight.Turns.Finished) return;
            if (fight.Sequence is not null) { fight.EndRequested = true; return; }
            EndTurn(selection, fight);
        }
    }
    private void EndTurn(Selection selection, FightSession fight)
    {
        if (fight.AwaitingReady) return;
        fight.AwaitingReady = true; fight.ReadyDeadline = After(combatTiming.ReadyMilliseconds);
        selection.Emit(new FightTurnEnding());
    }
    internal void CombatTurnReady(Selection selection, bool ready)
    {
        lock (gate)
        {
            Require(selection);
            if (ready && selection.Fight is { Turns: not null, AwaitingReady: true, Sequence: null } fight) NextTurn(selection, fight);
        }
    }
    private void NextTurn(Selection selection, FightSession fight)
    {
        var turns = fight.Turns!;
        if (turns.Next(turns.Current.Id, turns.Serial)) PublishTurn(selection, fight, true);
    }
}
