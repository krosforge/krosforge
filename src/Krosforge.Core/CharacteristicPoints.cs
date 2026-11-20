namespace Krosforge.Core;

public sealed record CharacteristicPoints(int Vitality = 0, int Chance = 0, int Agility = 0,
    int Intelligence = 0, int Strength = 0, int Wisdom = 0)
{
    public static CharacteristicPoints Empty { get; } = new();
    public static int Earned(int level) => checked((level - 1) * 5);

    public bool Fits(int level) => Vitality >= 0 && Chance >= 0 && Agility >= 0 && Intelligence >= 0 && Strength >= 0 && Wisdom >= 0
        && Spent <= Earned(level) && Wisdom % 3 == 0 && ExactCost(Chance) && ExactCost(Agility)
        && ExactCost(Intelligence) && ExactCost(Strength);

    public int Available(int level) => checked((int)(Earned(level) - Spent));
    public CombatStatistics Statistics
    {
        get
        {
            var strength = ElementalValue(Strength);
            var intelligence = ElementalValue(Intelligence);
            var chance = ElementalValue(Chance);
            var agility = ElementalValue(Agility);
            return CombatStatistics.Empty with
            {
                Initiative = checked(strength + intelligence + chance + agility),
                Strength = strength, Intelligence = intelligence, Chance = chance, Agility = agility,
                Vitality = Vitality, Wisdom = Wisdom / 3
            };
        }
    }

    private long Spent => (long)Vitality + Chance + Agility + Intelligence + Strength + Wisdom;
    private static bool ExactCost(int points) => points switch
    {
        <= 100 => true,
        <= 300 => (points - 100) % 2 == 0,
        <= 600 => (points - 300) % 3 == 0,
        _ => (points - 600) % 4 == 0
    };

    private static int ElementalValue(int points) => points switch
    {
        <= 100 => points,
        <= 300 => 100 + (points - 100) / 2,
        <= 600 => 200 + (points - 300) / 3,
        _ => 300 + (points - 600) / 4
    };
}
