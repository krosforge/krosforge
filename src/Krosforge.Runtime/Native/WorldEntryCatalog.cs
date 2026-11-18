using System.Text.Json;
using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed record EntryAttributes(int Life, int ActionPoints, int MovementPoints, long NextLevelExperience);
internal sealed record EntryMap(long Id, int Subarea, IReadOnlySet<int> WalkableCells)
{
    internal MovementGrid? Movement { get; init; }
}
internal sealed record MapTransition(long Source, long Destination, string Side, int[] ExitCells, int ArrivalCell, int Direction);
internal sealed class WorldEntryCatalog
{
    private sealed record Configuration(string Build, string GeometryBundle, string GeometryHash,
        string InformationBundle, string InformationHash, string ExperienceBundle, string ExperienceHash,
        WorldLocation Initial, EntryAttributes Attributes, MovementPolicy Movement, MapTransition[] Transitions);
    internal WorldLocation Initial { get; }
    internal EntryAttributes Attributes { get; }
    internal MovementPolicy Movement { get; }
    private readonly MapTransition[] transitions;
    private readonly Dictionary<long, EntryMap> maps;

    internal WorldEntryCatalog(string root)
    {
        using var resource = typeof(WorldEntryCatalog).Assembly.GetManifestResourceStream("Krosforge.Runtime.Native.world-entry.json")
            ?? throw new InvalidDataException("World entry configuration missing.");
        var config = JsonSerializer.Deserialize<Configuration>(resource) ?? throw new InvalidDataException("World entry configuration invalid.");
        if (config.Build != ClientInstallation.SupportedBuild) throw new InvalidDataException("World entry build mismatch.");
        Initial = config.Initial;
        Movement = config.Movement;
        if (Movement.Columns != 14 || Movement.MaximumCells is < 2 or > 560 || Movement.StepMilliseconds is < 1 or > 5000)
            throw new InvalidDataException("Invalid movement configuration.");
        transitions = config.Transitions;
        var experience = CreationDataReader.Read(Path.Combine(root, config.ExperienceBundle), config.ExperienceHash, includeKeys: true);
        var next = (double)experience.Single(row => (long)row["$key"] == 2)["experiencePoints"];
        if (!double.IsFinite(next) || next <= 0 || next >= long.MaxValue || Math.Truncate(next) != next)
            throw new InvalidDataException("Invalid experience threshold.");
        Attributes = config.Attributes with { NextLevelExperience = checked((long)next) };
        if (Attributes.Life <= 0 || Attributes.ActionPoints <= 0 || Attributes.MovementPoints <= 0)
            throw new InvalidDataException("Invalid initial attributes.");
        var information = CreationDataReader.Read(Path.Combine(root, config.InformationBundle), config.InformationHash);
        var ids = transitions.SelectMany(t => new[] { t.Source, t.Destination }).Append(Initial.MapId).Distinct().Select(id => checked((int)id)).ToArray();
        var geometry = CreationDataReader.ReadMaps(Path.Combine(root, config.GeometryBundle), config.GeometryHash, ids);
        maps = geometry.ToDictionary(row => (long)(int)row["id"], row =>
        {
            var id = (int)row["id"];
            var cells = ((object[])((Dictionary<string, object>)row["cellsData"])["common:49"]).Cast<Dictionary<string, object>>().ToArray();
            if (cells.Length != 560 || !cells.Select(c => (int)c["cellNumber"]).Order().SequenceEqual(Enumerable.Range(0, 560)))
                throw new InvalidDataException("Invalid cell geometry.");
            var allowed = cells.Where(c => (byte)c["mov"] == 1 && (byte)c["visible"] == 1 && (int)c["altitude"] != -128)
                .Select(c => (int)c["cellNumber"]).ToHashSet();
            var info = information.Single(m => (int)m["id"] == id);
            return new EntryMap(id, Convert.ToInt32(info["subAreaId"]), allowed)
            {
                Movement = new MovementGrid(Movement.Columns, cells.Where(c => allowed.Contains((int)c["cellNumber"]))
                    .Select(c => new MovementCell((int)c["cellNumber"], (int)c["altitude"], Convert.ToInt32(c["linkedZone"]))))
            };
        });
        Validate(Initial);
        foreach (var transition in transitions)
        {
            var row = geometry.Single(m => (int)m["id"] == transition.Source);
            var (field, mask, column) = transition.Side switch
            {
                "right" => ("rightNeighbourId", 131, 13),
                "left" => ("leftNeighbourId", 56, 0),
                _ => throw new InvalidDataException("Unsupported transition side.")
            };
            if (Convert.ToInt64(row[field]) != transition.Destination || transition.ExitCells.Length == 0 ||
                transition.ExitCells.Distinct().Count() != transition.ExitCells.Length)
                throw new InvalidDataException("Transition differs from installed geometry.");
            var cells = ((object[])((Dictionary<string, object>)row["cellsData"])["common:49"]).Cast<Dictionary<string, object>>().ToDictionary(c => (int)c["cellNumber"]);
            foreach (var cell in transition.ExitCells)
                if (!Map(transition.Source).WalkableCells.Contains(cell) || cell % Movement.Columns != column ||
                    (Convert.ToInt32(cells[cell]["mapChangeData"]) & mask) == 0)
                    throw new InvalidDataException("Invalid transition exit.");
            Validate(new(transition.Destination, transition.ArrivalCell, transition.Direction, 1));
        }
    }
    internal WorldEntryCatalog(WorldLocation initial, params EntryMap[] maps) : this(initial, new(14, 560, 200), [], maps) { }
    internal WorldEntryCatalog(WorldLocation initial, MovementPolicy movement, MapTransition[] transitions, params EntryMap[] maps)
    {
        Initial = initial; Attributes = new(100, 6, 3, 110); Movement = movement; this.transitions = transitions;
        this.maps = maps.ToDictionary(m => m.Id, m => m with { Movement = m.Movement ?? new(movement.Columns, m.WalkableCells.Select(id => new MovementCell(id, 0, 1))) });
        Validate(initial);
    }
    internal WorldLocation? Transition(WorldLocation source, long destination)
    {
        var route = transitions.SingleOrDefault(t => t.Source == source.MapId && t.Destination == destination && t.ExitCells.Contains(source.CellId));
        if (route is null) return null;
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
