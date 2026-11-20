using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using Krosforge.Core;
using Krosforge.Runtime.Native.Protocol.Protobuf;
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
            E("jwh", ResultStatistics(result.Character.Id, result.Summary)),
            E("irl", InventoryWire.Snapshot(result.Receipt.Balance, result.Receipt.State.Items)),
            E("ith", InventoryWire.Balance(result.Receipt.Balance)),
            .. result.LevelChanged ? Array.Empty<byte[]>() : [E("krg", WorldEntryWire.Statistics(result.Attributes, result.Character.Level))]];
    }

    private static byte[] ResultStatistics(long actor, CombatSummary summary)
    {
        var dealt = checked((int)summary.DamageDealt);
        var taken = checked((int)summary.DamageTaken);
        var healingDone = checked((int)summary.HealingDone);
        var healingReceived = checked((int)summary.HealingReceived);
        var defeated = summary.EnemiesDefeated;
        var turns = summary.TurnsStarted;
        var ap = summary.ActionPointsSpent;
        // jud fields populate the nine rows. The empty shield messages retain their rows
        // when the fight has no shield effects. Player identity is required for the portrait.
        var details = J(
            B(1, J(Average(1, dealt, ap), N(2, dealt), Average(6, dealt, turns), N(10, dealt))),
            B(2, J(Average(3, healingReceived, turns), N(4, healingReceived))),
            B(3, []),
            B(5, Average(2, summary.MovementPointsSpent, turns)),
            B(6, []),
            B(7, J(N(6, taken), Average(7, taken, turns))),
            B(8, J(N(1, actor), B(5, []), N(7, 0))),
            B(9, Average(2, ap, turns)),
            B(11, J(N(2, defeated), N(3, defeated))),
            B(12, J(Average(2, healingDone, turns), N(3, healingDone), Average(4, healingDone, ap))));
        return J(B(1, J(N(1, actor), B(2, details))), B(2, J(N(2, healingDone), N(3, healingReceived),
            N(4, dealt), N(5, taken), N(7, defeated))));
    }

    private static byte[] Average(int field, long amount, long count)
    {
        var writer = new ArrayBufferWriter<byte>();
        ProtoWriter.Varint(writer, ((ulong)field << 3) | 5);
        BinaryPrimitives.WriteSingleLittleEndian(writer.GetSpan(sizeof(float)), count == 0 ? 0 : (float)((double)amount / count));
        writer.Advance(sizeof(float));
        return writer.WrittenSpan.ToArray();
    }
}
