using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Krosforge.Contracts;
using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed class CombatRewardCalculator
{
    private sealed record Source(string Path, string Sha256, Dictionary<int, string> Formulas);
    private sealed record Configuration(string Build, Source Source, GroupExperienceRules Group, PlayerExperienceRules Player,
        ProspectingRules Prospecting, LootSources Loot);
    private readonly GroupExperienceRules group;
    private readonly PlayerExperienceRules player;
    private readonly ProspectingRules? prospecting;
    private readonly CombatLootCatalog? loot;
    private readonly Func<double> random = Random.Shared.NextDouble;
    private readonly Func<int, int, int> dice = (min, max) => Random.Shared.Next(min, checked(max + 1));
    private ServerSettings settings;
    internal CombatRewardCalculator(string root, ServerSettings settings, IEnumerable<int>? monsters = null)
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
        group = config.Group; player = config.Player; prospecting = config.Prospecting; this.settings = settings;
        if (monsters is not null) loot = new CombatLootCatalog(root, config.Loot, monsters);
    }
    internal CombatRewardCalculator(GroupExperienceRules group, PlayerExperienceRules player, ServerSettings settings,
        CombatLootCatalog? loot = null, ProspectingRules? prospecting = null, Func<double>? random = null, Func<int, int, int>? dice = null)
    {
        this.group = group; this.player = player; this.settings = settings; this.loot = loot; this.prospecting = prospecting;
        if (random is not null) this.random = random;
        if (dice is not null) this.dice = dice;
    }
    internal void Configure(ServerSettings settings) => this.settings = settings;
    internal RewardRates Presentation => new(ServerRewardRates.Percentage(settings.ExperienceMultiplier), ServerRewardRates.Percentage(settings.DropMultiplier));
    internal CombatAward Calculate(CombatEncounter encounter, int level, int wisdom, bool victory, int prospectingScore = 100,
        int hunter = 0, IReadOnlyList<InventoryItem>? inventory = null)
    {
        if (!victory) return new(0, []);
        var monsters = encounter.Opponents.Select(opponent => new ExperienceMonster(opponent.Monster.Level, opponent.Monster.Experience)).ToArray();
        var shared = CombatExperience.Group([new(level)], monsters, true, 1, group);
        var earned = CombatExperience.Individual(shared, monsters, level, wisdom, level, 1, 1, 1, player);
        var items = loot is null ? [] : encounter.Opponents.SelectMany(opponent => loot.Roll(opponent.Monster.Definition, opponent.Monster.Grade,
            new(level, encounter.Map, loot.ServerType, loot.Season, hunter, new HashSet<int>(), new HashSet<int>(), new HashSet<int>(), inventory ?? []),
            CombatDrops.ProspectingCoefficient(checked(prospectingScore - 100), prospecting ?? throw new InvalidOperationException("Prospecting formula missing.")),
            settings.DropMultiplier, random, dice)).ToArray();
        return new(CombatExperience.Multiply(earned, settings.ExperienceMultiplier), items);
    }
}
