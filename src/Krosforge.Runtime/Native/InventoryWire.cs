using System.Text.Json;
namespace Krosforge.Runtime.Native;

internal static class InventoryWire
{
    private static byte[] N(int tag, long value) => WorldProtocol.Number(tag, unchecked((ulong)value));
    private static byte[] B(int tag, byte[] value) => WorldProtocol.Bytes(tag, value);
    private static byte[] J(params byte[][] values) => WorldProtocol.Join(values);
    internal static byte[] Snapshot(long currency, IReadOnlyList<InventoryItem> items) =>
        J(items.Select(item => B(2, Item(item))).Concat([B(3, []), N(4, currency)]).ToArray());
    internal static byte[] Balance(long currency) => N(2, currency);
    private static byte[] Item(InventoryItem item)
    {
        if (item.Id is <= 0 or > int.MaxValue || item.Template <= 0 || item.Quantity <= 0)
            throw new InvalidDataException("Invalid inventory item.");
        var effects = JsonSerializer.Deserialize<InventoryEffect[]>(item.Effects) ?? throw new InvalidDataException("Invalid item effects.");
        var common = J(new[] { N(3, item.Quantity), N(4, item.Template), N(5, item.Id) }
            .Concat(effects.Select(effect => B(7, Effect(effect)))).ToArray());
        // CharacterInventoryPositionEnum has an Int16 literal 63 for the bag.
        return J(B(1, common), N(4, 63));
    }
    private static byte[] Effect(InventoryEffect effect)
    {
        if (effect.Action <= 0 || effect.Maximum != 0 && effect.Maximum < effect.Minimum)
            throw new InvalidDataException("Invalid item effect.");
        return J(N(10, effect.Action), effect.Maximum == 0 ? N(5, checked(effect.Minimum + effect.Constant))
            : B(9, J(N(1, effect.Minimum), N(2, effect.Maximum), N(4, effect.Constant))));
    }
    internal static byte[] Prize(IEnumerable<ItemAward> items) => B(1, J(items.GroupBy(item => item.Template)
        .Select(group => B(3, J(N(1, group.Aggregate(0L, (total, item) => checked(total + item.Quantity))), N(3, group.Key)))).ToArray()));
}
