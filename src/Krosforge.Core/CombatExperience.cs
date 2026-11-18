namespace Krosforge.Core;

public sealed record ExperienceParticipant(int Level, bool Companion = false, bool Present = true);
public sealed record ExperienceMonster(int Level, long Experience, bool Alive = false,
    double FamilyBonus = 1, double CalendarBonus = 1);
public sealed record GroupExperienceRules(IReadOnlyList<double> Bonuses, int LowerTolerance, int UpperTolerance);
public sealed record PlayerExperienceRules(double MonsterLevelCap, long MaximumExperience);

// Group and individual stages remain separate because each native formula floors
// its own result. Combining them before rounding changes the earned experience.
public static class CombatExperience
{
    public static long Group(IReadOnlyList<ExperienceParticipant> players, IReadOnlyList<ExperienceMonster> monsters,
        bool victory, double rewardRate, GroupExperienceRules rules)
    {
        Validate(players, monsters);
        if (!double.IsFinite(rewardRate) || rewardRate < 0 || rules.LowerTolerance < 0 || rules.UpperTolerance < 0 ||
            rules.Bonuses.Count == 0 || rules.Bonuses.Any(b => !double.IsFinite(b) || b < 0))
            throw new ArgumentException("Invalid experience rules.");
        var totalPlayers = players.Sum(p => (double)p.Level);
        var totalMonsters = monsters.Sum(m => (double)m.Level);
        var humans = players.Count(p => p.Level > players.Max(p => p.Level) / 3d && !p.Companion && p.Present);
        if (humans > rules.Bonuses.Count) throw new ArgumentException("Unsupported experience group size.");
        var ratio = totalPlayers - rules.LowerTolerance > totalMonsters ? totalMonsters / totalPlayers
            : totalMonsters > totalPlayers + rules.UpperTolerance ? (totalPlayers + rules.UpperTolerance) / totalMonsters : 1;
        var experience = monsters.Where(m => !m.Alive).Sum(m => m.Experience * m.FamilyBonus * m.CalendarBonus) * ratio;
        return Floor(experience * (victory ? humans > 0 ? rules.Bonuses[humans - 1] : 1 : 0) * rewardRate);
    }

    public static long Individual(long groupExperience, IReadOnlyList<ExperienceMonster> monsters, int playerLevel,
        int wisdom, int groupLevel, double challenge, double mapBonus, double calendarBonus, PlayerExperienceRules rules)
    {
        if (groupExperience < 0 || monsters.Count == 0 || monsters.Any(m => m.Level <= 0) || playerLevel <= 0 || groupLevel < 0 ||
            !double.IsFinite(rules.MonsterLevelCap) || rules.MonsterLevelCap <= 0 || rules.MaximumExperience <= 0 ||
            new[] { challenge, mapBonus, calendarBonus }.Any(v => !double.IsFinite(v) || v < 0))
            throw new ArgumentException("Invalid individual experience inputs.");
        var share = groupLevel > 0 ? challenge * (groupExperience * Math.Min(playerLevel, monsters.Max(m => m.Level) * rules.MonsterLevelCap) / groupLevel) : 1;
        if (wisdom > 0) share *= (100d + wisdom) / 100d;
        if (mapBonus > 1) share *= mapBonus;
        if (calendarBonus > 1) share *= calendarBonus;
        return Floor(Math.Min(Math.Max(1, share), rules.MaximumExperience));
    }

    public static long Multiply(long experience, decimal multiplier)
    {
        if (experience < 0 || multiplier < 0) throw new ArgumentOutOfRangeException(nameof(multiplier));
        return checked((long)decimal.Floor(experience * multiplier));
    }
    private static long Floor(double value) => double.IsFinite(value) && value >= 0 && value < long.MaxValue
        ? checked((long)Math.Floor(value)) : throw new OverflowException("Experience is out of range.");
    private static void Validate(IReadOnlyList<ExperienceParticipant> players, IReadOnlyList<ExperienceMonster> monsters)
    {
        if (players.Count == 0 || monsters.Count == 0 || players.Any(p => p.Level <= 0) ||
            monsters.Any(m => m.Level <= 0 || m.Experience < 0 || !double.IsFinite(m.FamilyBonus) || m.FamilyBonus < 0 ||
                !double.IsFinite(m.CalendarBonus) || m.CalendarBonus < 0))
            throw new ArgumentException("Invalid group experience inputs.");
    }
}
