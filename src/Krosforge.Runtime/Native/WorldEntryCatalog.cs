using System.Text.Json;
using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed record EntryAttributes(int Life, int ActionPoints, int MovementPoints, long NextLevelExperience)
{
    public int Energy { get; init; } = 10000;
    public int Prospecting { get; init; } = 100;
    public int Hunter { get; init; }
    public long Experience { get; init; }
    public long ExperienceFloor { get; init; }
    public long Currency { get; init; }
}
internal sealed record EntryMap(long Id, int Subarea, IReadOnlySet<int> WalkableCells)
{
    internal MovementGrid? Movement { get; init; }
    internal CombatBoard? Combat { get; init; }
}
internal sealed record MapTransition(long Source, long Destination, string Side, int ExitCell, int ArrivalCell, int Direction);
internal sealed class WorldEntryCatalog
{
    private sealed record Configuration(string Build, string GeometryBundle, string GeometryHash,
        string InformationBundle, string InformationHash, string ExperienceBundle, string ExperienceHash,
        WorldLocation Initial, EntryAttributes Attributes, MovementPolicy Movement, int WorldMap, SurfaceMapDefinition[] SurfaceMaps,
        int MaximumCharacterLevel);
    internal WorldLocation Initial { get; }
    internal EntryAttributes Attributes { get; }
    internal ExperienceCurve Experience { get; }
    internal MovementPolicy Movement { get; }
    private readonly Dictionary<(long Source, long Destination, int Cell), MapTransition> transitions;
    internal IReadOnlyCollection<MapTransition> Transitions => transitions.Values;
    internal IReadOnlyCollection<EntryMap> Maps => maps.Values;
    private readonly Dictionary<long, EntryMap> maps;

    internal WorldEntryCatalog(string root)
    {
        using var resource = typeof(WorldEntryCatalog).Assembly.GetManifestResourceStream("Krosforge.Runtime.Native.world-entry.json")
            ?? throw new InvalidDataException("World entry configuration missing.");
        var config = JsonSerializer.Deserialize<Configuration>(resource) ?? throw new InvalidDataException("World entry configuration invalid.");
        if (config.Build != ClientInstallation.SupportedBuild) throw new InvalidDataException("World entry build mismatch.");
        Initial = config.Initial;
        Movement = config.Movement;
        if (Movement.Columns != 14 || Movement.MaximumCells is < 2 or > 560 || Movement.StepMilliseconds is < 1 or > 5000 ||
            Movement.CancellationLeadSteps is < 0 or > 1 || Movement.PredictionLeadMilliseconds is < 0 or > 100 || Movement.Timing is not { IsValid: true })
            throw new InvalidDataException("Invalid movement configuration.");
        var experience = CreationDataReader.Read(Path.Combine(root, config.ExperienceBundle), config.ExperienceHash, includeKeys: true);
        if (config.MaximumCharacterLevel is < 2 or > 1000) throw new InvalidDataException("Invalid maximum character level.");
        var levels = experience.Where(row => (long)row["$key"] <= config.MaximumCharacterLevel).OrderBy(row => (long)row["$key"]).ToArray();
        if (!levels.Select(row => (long)row["$key"]).SequenceEqual(Enumerable.Range(1, config.MaximumCharacterLevel).Select(n => (long)n)))
            throw new InvalidDataException("Missing experience thresholds.");
        Experience = new(levels.Select(row =>
        {
            var value = (double)row["experiencePoints"];
            if (!double.IsFinite(value) || value < 0 || value >= long.MaxValue || Math.Truncate(value) != value)
                throw new InvalidDataException("Invalid experience threshold.");
            return checked((long)value);
        }));
        var next = (double)experience.Single(row => (long)row["$key"] == 2)["experiencePoints"];
        if (!double.IsFinite(next) || next <= 0 || next >= long.MaxValue || Math.Truncate(next) != next)
            throw new InvalidDataException("Invalid experience threshold.");
        Attributes = config.Attributes with { NextLevelExperience = checked((long)next) };
        if (Attributes.Life <= 0 || Attributes.ActionPoints <= 0 || Attributes.MovementPoints <= 0)
            throw new InvalidDataException("Invalid initial attributes.");
        var information = CreationDataReader.Read(Path.Combine(root, config.InformationBundle), config.InformationHash);
        if (config.SurfaceMaps is null || config.SurfaceMaps.Length == 0) throw new InvalidDataException("Missing surface maps.");
        var ids = config.SurfaceMaps.Select(m => checked((int)m.Id)).ToArray();
        var geometry = CreationDataReader.ReadMaps(Path.Combine(root, config.GeometryBundle), config.GeometryHash, ids);
        var installed = geometry.ToDictionary(row => (long)(int)row["id"], row =>
        {
            var id = (int)row["id"];
            var info = information.Single(m => (int)m["id"] == id);
            var definition = config.SurfaceMaps.Single(m => m.Id == id);
            if (Convert.ToInt32(info["worldMap"]) != config.WorldMap || Convert.ToInt32(info["posX"]) != definition.X || Convert.ToInt32(info["posY"]) != definition.Y)
                throw new InvalidDataException("Surface map differs from installed world coordinates.");
            return SurfaceMapGeometry.Read(row, info);
        });
        maps = installed.Values.ToDictionary(m => m.Id, m => new EntryMap(m.Id, m.Subarea,
            m.Cells.Where(c => c.Walkable).Select(c => c.Id).ToHashSet())
        {
            Combat = CombatCatalog.ReadBoard(geometry.Single(row => (int)row["id"] == m.Id)),
            Movement = new MovementGrid(Movement.Columns, m.Cells.Where(c => c.Walkable)
                .Select(c => new MovementCell(c.Id, c.Altitude, c.RoleplayZone)))
        });
        transitions = SurfaceTopology.Build(config.SurfaceMaps, installed).ToDictionary(t => (t.Source, t.Destination, t.ExitCell));
        Validate(Initial);
    }
    internal WorldEntryCatalog(WorldLocation initial, params EntryMap[] maps) : this(initial, new(14, 560, 200), [], maps) { }
    internal WorldEntryCatalog(WorldLocation initial, MovementPolicy movement, MapTransition[] transitions, params EntryMap[] maps)
    {
        Initial = initial; Attributes = new(100, 6, 3, 110); Experience = new([0, 110]); Movement = movement; this.transitions = transitions.ToDictionary(t => (t.Source, t.Destination, t.ExitCell));
        this.maps = maps.ToDictionary(m => m.Id, m => m with { Movement = m.Movement ?? new(movement.Columns, m.WalkableCells.Select(id => new MovementCell(id, 0, 1))) });
        Validate(initial);
    }
    internal WorldLocation? Transition(WorldLocation source, long destination)
    {
        if (!transitions.TryGetValue((source.MapId, destination, source.CellId), out var route)) return null;
        var next = new WorldLocation(destination, route.ArrivalCell, route.Direction, checked(source.Revision + 1));
        Validate(next); return next;
    }
    internal EntryMap Map(long id) => maps.TryGetValue(id, out var map) ? map : throw new InvalidDataException("Unsupported saved map.");
    internal void Validate(WorldLocation location)
    {
        location.Validate();
        if (!Map(location.MapId).WalkableCells.Contains(location.CellId)) throw new InvalidDataException("Saved cell is not walkable.");
    }
}
