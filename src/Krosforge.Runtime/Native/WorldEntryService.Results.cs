using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed record FightResultFighter(long Id, bool Alive, bool Won, int? Level);
internal sealed record FightCompleted(CharacterWire Character, EntryAttributes Attributes, IReadOnlyList<FightResultFighter> Fighters,
    CombatReceipt Receipt, CombatSummary Summary) : WorldNotice
{
    internal bool LevelChanged { get; init; }
}

internal sealed partial class WorldEntryService
{
    private void CommitCombat(Selection selection, FightSession fight, bool forfeit)
    {
        if (fight.Receipt is not null) return;
        var turns = fight.Turns;
        if (!forfeit && turns is not { Finished: true }) throw new InvalidOperationException("Combat has no result.");
        var won = turns is { Finished: true, WinningTeam: 0 };
        var status = won ? "victory" : forfeit && turns is not { Finished: true } ? "abandoned" : "defeat";
        var duration = fight.Started ? Math.Max(0, (long)clock.GetElapsedTime(fight.StartedAt, clock.GetTimestamp()).TotalMilliseconds) : 0;
        var attributes = selection.Attributes;
        fight.Award ??= rewards?.Calculate(fight.Encounter, selection.Character.Level, attributes.Statistics.Wisdom, won,
            attributes.Prospecting, attributes.Hunter, selection.Rewards.Items) ?? new(0, []);
        fight.Receipt = outcomes!.Complete(selection.Profile, ServerPresentation.RealmId, selection.Character.Id,
            fight.DurableId ?? throw new InvalidOperationException("Combat has no durable identity."), status,
            won ? fight.Encounter.Reward.Currency : 0, duration,
            award: fight.Award);
        selection.Currency = fight.Receipt.Balance;
        selection.Rewards = fight.Receipt.State;
        selection.Character = selection.Character with { Level = catalog.Experience.At(selection.Rewards.Experience).Level };
    }
    private void FinishCombat(Selection selection, FightSession fight, bool forfeit)
    {
        CommitCombat(selection, fight, forfeit);
        var won = fight.Receipt!.Status == "victory";
        var fighters = fight.Preparation.Fighters.Select(p => new FightResultFighter(p.ActorId,
            fight.Turns?.Fighters.Single(f => f.Id == p.ActorId).Alive ?? true,
            p.Controlled == won, p.Controlled ? selection.Character.Level : null)).ToArray();
        selection.PendingLevelUp = selection.Character.Level > fight.InitialLevel;
        selection.Emit(new FightCompleted(selection.Character, selection.Attributes, fighters, fight.Receipt,
            fight.Turns?.Summary(selection.Character.Id) ?? new()) { LevelChanged = selection.PendingLevelUp });
        ReturnFromFight(selection);
    }
}
