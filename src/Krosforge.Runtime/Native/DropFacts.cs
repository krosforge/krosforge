using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed record DropFacts(int Level, long Map, int ServerType, int Season, int Hunter,
    IReadOnlySet<int> ActiveQuests, IReadOnlySet<int> Alterations, IReadOnlySet<int> States, IReadOnlyList<InventoryItem> Inventory)
{
    internal bool? Evaluate(DropCriterion criterion)
    {
        var args = criterion.Arguments;
        bool Compare(long current, long expected) => criterion.Operator switch
        {
            '=' => current == expected, '!' => current != expected, '<' => current < expected, '>' => current > expected,
            _ => throw new InvalidDataException("Unsupported drop comparison.")
        };
        bool Presence(IReadOnlySet<int> values)
        {
            if (args.Count != 1 || args[0] is < 0 or > int.MaxValue || criterion.Operator is not ('=' or '!'))
                throw new InvalidDataException("Unsupported drop presence condition.");
            return criterion.Operator == '=' == values.Contains((int)args[0]);
        }
        if (criterion.Code == "PO")
        {
            if (args.Count is < 1 or > 2) throw new InvalidDataException("Unsupported inventory criterion.");
            var quantity = Inventory.Where(item => item.Template == args[0]).Sum(item => (long)item.Quantity);
            if (args.Count == 1)
                return criterion.Operator switch { '=' => quantity > 0, '!' => quantity == 0, _ => throw new InvalidDataException("Inventory count required.") };
            return Compare(quantity, args[1]);
        }
        if (criterion.Code == "Pj") return null; // Profession progress is not supplied by the current character state.
        if (criterion.Code == "Qa") return Presence(ActiveQuests);
        if (criterion.Code == "HA") return Presence(Alterations);
        if (criterion.Code == "HS") return Presence(States);
        if (args.Count != 1) return null;
        return criterion.Code switch
        {
            "PL" => Compare(Level, args[0]), "Pm" => Compare(Map, args[0]),
            "SC" => Compare(ServerType, args[0]), "ST" => Compare(Season, args[0]),
            "CU" => Compare(Hunter, args[0]), _ => null
        };
    }
}
