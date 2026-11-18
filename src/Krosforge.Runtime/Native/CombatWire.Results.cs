using System.Text;
namespace Krosforge.Runtime.Native;

internal static partial class CombatWire
{
    private static byte[][] ResultEvents(FightCompleted result)
    {
        var won = result.Receipt.Status == "victory";
        var team = B(2, J(B(1, B(1, Encoding.UTF8.GetBytes(result.Character.Name))), N(2, won ? 2 : 0)));
        var fighters = result.Fighters.Select(f => B(4, J(N(1, f.Won ? 2 : 0),
            B(2, J(N(2, f.Alive ? 1 : 0), N(5, f.Id), f.Level is { } level ? B(3, N(2, level)) : [])),
            f.Id == result.Character.Id ? B(3, N(2, result.Receipt.Reward)) : []))).ToArray();
        return [E("jwp", J(team, J(fighters), N(8, result.Receipt.DurationMilliseconds))),
            E("irl", WorldEntryWire.EmptyInventory(result.Receipt.Balance)),
            E("krg", WorldEntryWire.Statistics(result.Attributes, result.Character.Level))];
    }
}
