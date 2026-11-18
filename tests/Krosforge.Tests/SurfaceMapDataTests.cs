using System.Security.Cryptography;
using System.Text;
using Krosforge.Core;
using Krosforge.Runtime.Native;
using Xunit;

namespace Krosforge.Tests;

public sealed class SurfaceMapDataTests
{
    internal static WorldEntryCatalog ReferenceCatalog()
    {
        var root = Environment.GetEnvironmentVariable("KROSFORGE_TEST_CLIENT");
        Assert.SkipWhen(root is null, "Requires the separately installed reference client; no game assets in CI.");
        return new(root!);
    }

    // Test traversal uses the movement validator too: a connected map graph alone
    // cannot prove that an arriving character can actually reach the next exit.
    internal static int[] FindPath(EntryMap map, MovementPolicy policy, int origin, int destination)
    {
        var grid = map.Movement!;
        var coordinates = map.WalkableCells.ToDictionary(grid.Coordinate);
        var previous = new Dictionary<int, int> { [origin] = -1 };
        var queue = new Queue<int>(); queue.Enqueue(origin);
        while (queue.TryDequeue(out var cell) && !previous.ContainsKey(destination))
        {
            var (x, y) = grid.Coordinate(cell);
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    if ((dx != 0 || dy != 0) && coordinates.TryGetValue((x + dx, y + dy), out var next) &&
                        !previous.ContainsKey(next) && grid.Validate(cell, [cell, next], 0, policy) is not null)
                    { previous.Add(next, cell); queue.Enqueue(next); }
        }
        Assert.True(previous.ContainsKey(destination), $"No walking path in map {map.Id}: {origin} -> {destination}");
        var result = new List<int>();
        for (var cell = destination; cell >= 0; cell = previous[cell]) result.Add(cell);
        result.Reverse(); return result.ToArray();
    }

    [Fact]
    public void InstalledSurfaceMatchesTheIndependentlyExtractedBoundaryContract()
    {
        var catalog = ReferenceCatalog();
        Assert.Equal(50, catalog.Maps.Count);
        Assert.Equal(156, catalog.Transitions.Select(t => (t.Source, t.Destination)).Distinct().Count());
        Assert.Equal(2481, catalog.Transitions.Count);
        var serialized = string.Concat(catalog.Transitions.OrderBy(t => t.Source).ThenBy(t => t.Destination).ThenBy(t => t.ExitCell)
            .Select(t => FormattableString.Invariant($"{t.Source},{t.Destination},{t.ExitCell},{t.ArrivalCell},{t.Direction}\n")));
        Assert.Equal("5f12eac74ef85f16cbd081eab43adabfd72a6253500ad128e85747f9ec91ef7b",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(serialized))));
        foreach (var route in catalog.Transitions)
        {
            var location = catalog.Transition(new(route.Source, route.ExitCell, 0, 10), route.Destination);
            Assert.Equal(new WorldLocation(route.Destination, route.ArrivalCell, route.Direction, 11), location);
            catalog.Validate(location!);
        }
        foreach (var excluded in new long[] { 153884672, 153883908, 152833028, 153486338, 222567434, 122684931 })
            Assert.Throws<InvalidDataException>(() => catalog.Map(excluded));
    }

    [Fact]
    public void EverySurfaceExitIsReachableOnFootFromTheInitialCharacterPosition()
    {
        var catalog = ReferenceCatalog();
        var byCoordinate = catalog.Maps.ToDictionary(m => m.Id, m => m.WalkableCells.ToDictionary(m.Movement!.Coordinate));
        var transitions = catalog.Transitions.ToLookup(r => (r.Source, r.ExitCell));
        var reached = new HashSet<(long Map, int Cell)> { (catalog.Initial.MapId, catalog.Initial.CellId) };
        var queue = new Queue<(long Map, int Cell)>(reached);
        while (queue.TryDequeue(out var position))
        {
            var map = catalog.Map(position.Map); var (x, y) = map.Movement!.Coordinate(position.Cell);
            void Visit(long id, int cell) { if (reached.Add((id, cell))) queue.Enqueue((id, cell)); }
            foreach (var route in transitions[position]) Visit(route.Destination, route.ArrivalCell);
            for (var dx = -1; dx <= 1; dx++)
                for (var dy = -1; dy <= 1; dy++)
                    if ((dx != 0 || dy != 0) && byCoordinate[map.Id].TryGetValue((x + dx, y + dy), out var next) &&
                        map.Movement.Validate(position.Cell, [position.Cell, next], 0, catalog.Movement) is not null)
                        Visit(map.Id, next);
        }
        Assert.Equal(50, reached.Select(p => p.Map).Distinct().Count());
        Assert.Equal(16019, reached.Count);
        Assert.All(catalog.Transitions, route => Assert.Contains((route.Source, route.ExitCell), reached));
    }
}
