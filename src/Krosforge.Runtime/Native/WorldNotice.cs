using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed record WorldActor(CharacterWire Character, long Account, WorldLocation Location);
internal abstract record WorldNotice;
internal sealed record RewardRates(int ExperiencePercent = 100, int DropPercent = 100);
internal sealed record MapSnapshot(EntryMap Map, IReadOnlyList<WorldActor> Actors, IReadOnlyList<CombatEncounter>? Encounters = null, int? NeutralCalendar = null) : WorldNotice
{
    internal RewardRates? Rates { get; init; }
}
internal sealed record ActorAppeared(WorldActor Actor) : WorldNotice;
internal sealed record ActorDeparted(long Id) : WorldNotice;
internal sealed record ActorMoved(long Id, IReadOnlyList<int> Cells) : WorldNotice;
internal sealed record ActorCorrected(long Id, int Cell) : WorldNotice;
internal sealed record MovementReleased : WorldNotice;
internal sealed record MapLoading(long Map) : WorldNotice;
internal sealed record CharacterStats(EntryAttributes Attributes, int Level) : WorldNotice;
internal sealed record CharacterLeveled(int Level, EntryAttributes Attributes) : WorldNotice;
internal sealed record RoleplaySpellBook(IReadOnlyList<RoleplaySpell> Spells) : WorldNotice;
internal sealed record SpellBarSnapshot(IReadOnlyList<SpellShortcut> Shortcuts) : WorldNotice;
internal sealed record BattleMapPreview(long Source, BattleMap Battle) : WorldNotice;
internal sealed record EncounterAppeared(CombatEncounter Encounter) : WorldNotice;
internal sealed record EncounterDeparted(long Group) : WorldNotice;
internal sealed record FightEntered(long Id, CombatEncounter Encounter) : WorldNotice;
internal sealed record FightLoaded(CombatView View) : WorldNotice;
internal sealed record FightPositions(IReadOnlyList<CombatPlacement> Positions) : WorldNotice;
internal sealed record FightStarted(IReadOnlyList<long> Order) : WorldNotice;
internal sealed record FightLeft(long Map) : WorldNotice;
