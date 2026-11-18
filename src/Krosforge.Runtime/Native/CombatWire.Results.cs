using System.Text;
namespace Krosforge.Runtime.Native;

internal static partial class CombatWire
{
    private static byte[][] ResultEvents(FightCompleted result)
    {
        var won = result.Receipt.Status == "victory";
        var team = B(2, J(B(1, B(1, Encoding.UTF8.GetBytes(result.Character.Name))), N(2, won ? 2 : 0)));
        var experience = B(1, B(1, J(N(3, result.Receipt.State.Experience), N(4, result.Attributes.ExperienceFloor),
            N(5, 1), N(6, result.Attributes.NextLevelExperience), N(8, result.Receipt.Award.Experience), N(9, 1))));
        var fighters = result.Fighters.Select(f => B(4, J(N(1, f.Won ? 2 : 0),
            B(2, J(N(2, f.Alive ? 1 : 0), N(5, f.Id), f.Level is { } level ? B(3, J(N(2, level), f.Id == result.Character.Id ? experience : [])) : [])),
            f.Id == result.Character.Id ? B(3, J(N(2, result.Receipt.Reward),
                result.Receipt.Award.Items.Length > 0 ? InventoryWire.Prize(result.Receipt.Award.Items) : [])) : []))).ToArray();
        return [E("jwp", J(team, J(fighters), N(8, result.Receipt.DurationMilliseconds))),
            E("jwh", B(2, J(N(4, checked((int)result.Summary.DamageDealt)),
                N(5, checked((int)result.Summary.DamageTaken)), N(7, result.Summary.EnemiesDefeated)))),
            E("irl", InventoryWire.Snapshot(result.Receipt.Balance, result.Receipt.State.Items)),
            E("krg", WorldEntryWire.Statistics(result.Attributes, result.Character.Level))];
    }
}
