using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Krosforge.Contracts;
using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed class CombatRewardCalculator
{
    private sealed record Source(string Path, string Sha256, Dictionary<int, string> Formulas);
    private sealed record Configuration(string Build, Source Source, GroupExperienceRules Group, PlayerExperienceRules Player);
    private readonly GroupExperienceRules group;
    private readonly PlayerExperienceRules player;
    private ServerSettings settings;
    internal CombatRewardCalculator(string root, ServerSettings settings)
    {
        using var resource = typeof(CombatRewardCalculator).Assembly.GetManifestResourceStream("Krosforge.Runtime.Native.combat-rewards.json")
            ?? throw new InvalidDataException("Reward configuration missing.");
        var config = JsonSerializer.Deserialize<Configuration>(resource) ?? throw new InvalidDataException("Invalid reward configuration.");
        if (config.Build != ClientInstallation.SupportedBuild) throw new InvalidDataException("Reward build mismatch.");
        var formulas = CreationDataReader.Read(Path.Combine(root, config.Source.Path), config.Source.Sha256, includeKeys: true,
            selectedKeys: config.Source.Formulas.Keys.Select(id => (long)id).ToHashSet());
        foreach (var (id, hash) in config.Source.Formulas)
        {
            var row = formulas.Single(row => (long)row["$key"] == id);
            if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((string)row["formula"]))) != hash)
                throw new InvalidDataException("Unsupported reward formula.");
        }
        group = config.Group; player = config.Player; this.settings = settings;
    }
    internal CombatRewardCalculator(GroupExperienceRules group, PlayerExperienceRules player, ServerSettings settings)
    { this.group = group; this.player = player; this.settings = settings; }
    internal void Configure(ServerSettings settings) => this.settings = settings;
    internal CombatAward Calculate(CombatEncounter encounter, int level, int wisdom, bool victory)
    {
        if (!victory) return new(0, []);
        ExperienceMonster[] monsters = [new(encounter.Monster.Level, encounter.Monster.Experience)];
        var shared = CombatExperience.Group([new(level)], monsters, true, 1, group);
        var earned = CombatExperience.Individual(shared, monsters, level, wisdom, level, 1, 1, 1, player);
        return new(CombatExperience.Multiply(earned, settings.ExperienceMultiplier), []);
    }
}
