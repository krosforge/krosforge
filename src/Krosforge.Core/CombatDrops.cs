namespace Krosforge.Core;

public sealed record ProspectingRules(double Slope, double Midpoint, double MaximumPercent,
    double Amplitude, double Offset, double MaximumCoefficient);

public static class CombatDrops
{
    public static double ProspectingCoefficient(int bonus, ProspectingRules rules)
    {
        if (rules.Slope <= 0 || rules.MaximumCoefficient < 1 ||
            new[] { rules.Slope, rules.Midpoint, rules.MaximumPercent, rules.Amplitude, rules.Offset, rules.MaximumCoefficient }
                .Any(value => !double.IsFinite(value))) throw new ArgumentException("Invalid prospecting formula.");
        var sigmoid = 1d / (1d + Math.Exp(-(bonus - rules.Midpoint) / rules.Slope));
        return Math.Clamp(1d + rules.Amplitude * rules.MaximumPercent * sigmoid / 100d - rules.Offset, 1d, rules.MaximumCoefficient);
    }
    public static double Chance(double basePercent, double coefficient, decimal serverMultiplier)
    {
        if (!double.IsFinite(basePercent) || basePercent is < 0 or > 100 || !double.IsFinite(coefficient) || coefficient < 0 || serverMultiplier < 0)
            throw new ArgumentException("Invalid drop probability.");
        return Math.Min(100, basePercent * coefficient * (double)serverMultiplier);
    }
    public static bool Roll(double chancePercent, Func<double> random)
    {
        if (!double.IsFinite(chancePercent) || chancePercent is < 0 or > 100) throw new ArgumentException("Invalid drop probability.");
        if (chancePercent == 0) return false;
        if (chancePercent == 100) return true;
        var sample = random();
        if (!double.IsFinite(sample) || sample is < 0 or >= 1) throw new ArgumentException("Invalid random sample.");
        return sample < chancePercent / 100d;
    }
}
