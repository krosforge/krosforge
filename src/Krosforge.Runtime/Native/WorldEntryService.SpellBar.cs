namespace Krosforge.Runtime.Native;

internal sealed partial class WorldEntryService
{
    private SpellShortcut[] SpellBar(Selection selection) => selection.Rewards.SpellBar ??
        spells?.Player(selection.Character.BreedId, selection.Character.Level).Select((spell, slot) => new SpellShortcut(slot, spell.Id)).ToArray() ?? [];

    internal void AssignSpell(Selection selection, SpellShortcut shortcut) => EditSpellBar(selection, before =>
        spells!.RoleplaySpells(selection.Character.BreedId, selection.Character.Level).Any(spell => spell.Id == shortcut.Spell)
            ? before.Where(row => row.Slot != shortcut.Slot && row.Spell != shortcut.Spell).Append(shortcut)
            : before);

    internal void RemoveSpell(Selection selection, int slot) => EditSpellBar(selection, before => before.Where(row => row.Slot != slot));

    internal void MoveSpell(Selection selection, int source, int destination) => EditSpellBar(selection, before =>
        before.Any(row => row.Slot == source)
            ? before.Select(row => row.Slot == source ? row with { Slot = destination }
                : row.Slot == destination ? row with { Slot = source } : row)
            : before);

    private void EditSpellBar(Selection selection, Func<SpellShortcut[], IEnumerable<SpellShortcut>> edit)
    {
        lock (gate)
        {
            Require(selection);
            if (!selection.MapReady || selection.Fight is not null || outcomes is null || spells is null) return;
            var before = SpellBar(selection);
            var after = edit(before).OrderBy(row => row.Slot).ToArray();
            if (after.Length <= 256 && !before.SequenceEqual(after))
            {
                outcomes.SaveSpellBar(selection.Profile, ServerPresentation.RealmId, selection.Character.Id, after);
                selection.Rewards = selection.Rewards with { SpellBar = after };
            }
            selection.Emit(new SpellBarSnapshot(SpellBar(selection)));
        }
    }
}
