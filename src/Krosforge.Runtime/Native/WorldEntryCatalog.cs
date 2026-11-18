using System.Text.Json;
using Krosforge.Core;
namespace Krosforge.Runtime.Native;

internal sealed record EntryAttributes(int Life, int ActionPoints, int MovementPoints, long NextLevelExperience);
internal sealed record EntryMap(long Id, int Subarea, IReadOnlySet<int> WalkableCells);
internal sealed class WorldEntryCatalog
{
    private sealed record Configuration(string Build, string GeometryBundle, string GeometryHash,
        string InformationBundle, string InformationHash, string ExperienceBundle, string ExperienceHash,
        WorldLocation Initial, EntryAttributes Attributes);
    internal WorldLocation Initial { get; }
    internal EntryAttributes Attributes { get; }
    private readonly Dictionary<long, EntryMap> maps;

    internal WorldEntryCatalog(string root)
    {
        using var resource = typeof(WorldEntryCatalog).Assembly.GetManifestResourceStream("Krosforge.Runtime.Native.world-entry.json")
            ?? throw new InvalidDataException("World entry configuration missing.");
        var config = JsonSerializer.Deserialize<Configuration>(resource) ?? throw new InvalidDataException("World entry configuration invalid.");
        if (config.Build != ClientInstallation.SupportedBuild) throw new InvalidDataException("World entry build mismatch.");
        Initial = config.Initial;
        var experience = CreationDataReader.Read(Path.Combine(root, config.ExperienceBundle), config.ExperienceHash, includeKeys: true);
        var next = (double)experience.Single(row => (long)row["$key"] == 2)["experiencePoints"];
        if (!double.IsFinite(next) || next <= 0 || next >= long.MaxValue || Math.Truncate(next) != next)
            throw new InvalidDataException("Invalid experience threshold.");
        Attributes = config.Attributes with { NextLevelExperience = checked((long)next) };
        if (Attributes.Life <= 0 || Attributes.ActionPoints <= 0 || Attributes.MovementPoints <= 0)
            throw new InvalidDataException("Invalid initial attributes.");
        var information = CreationDataReader.Read(Path.Combine(root, config.InformationBundle), config.InformationHash);
        var geometry = CreationDataReader.ReadMaps(Path.Combine(root, config.GeometryBundle), config.GeometryHash, checked((int)Initial.MapId));
        maps = geometry.ToDictionary(row => (long)(int)row["id"], row =>
        {
            var id = (int)row["id"];
            var cells = ((object[])((Dictionary<string, object>)row["cellsData"])["common:49"]).Cast<Dictionary<string, object>>().ToArray();
            if (cells.Length != 560 || !cells.Select(c => (int)c["cellNumber"]).Order().SequenceEqual(Enumerable.Range(0, 560)))
                throw new InvalidDataException("Invalid cell geometry.");
            var allowed = cells.Where(c => (byte)c["mov"] == 1 && (byte)c["visible"] == 1 && (int)c["altitude"] != -128)
                .Select(c => (int)c["cellNumber"]).ToHashSet();
            var info = information.Single(m => (int)m["id"] == id);
            return new EntryMap(id, Convert.ToInt32(info["subAreaId"]), allowed);
        });
        Validate(Initial);
    }
    internal WorldEntryCatalog(WorldLocation initial, params EntryMap[] maps)
    { Initial = initial; Attributes = new(100, 6, 3, 110); this.maps = maps.ToDictionary(m => m.Id); Validate(initial); }
    internal EntryMap Map(long id) => maps.TryGetValue(id, out var map) ? map : throw new InvalidDataException("Unsupported saved map.");
    internal void Validate(WorldLocation location)
    {
        location.Validate();
        if (!Map(location.MapId).WalkableCells.Contains(location.CellId)) throw new InvalidDataException("Saved cell is not walkable.");
    }
}
