using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed class MapDataTests
{
    [Fact]
    public void InstalledGeometryContainsTheConfiguredEntryAndAdjacentMap()
    {
        var root = Environment.GetEnvironmentVariable("KROSFORGE_TEST_CLIENT");
        Assert.SkipWhen(root is null, "Requires the separately installed reference client; no game assets in CI.");
        var catalog = new WorldEntryCatalog(root!);
        Assert.Equal(450, catalog.Map(catalog.Initial.MapId).Subarea);
        catalog.Validate(catalog.Initial);
        var path = Path.Combine(root!, "Dofus_Data", "StreamingAssets", "Content", "Map", "Data", "mapdata_assets_world_587.bundle");
        var maps = CreationDataReader.ReadMaps(path, "6806b94ebbfc904e0ba3f906942593819c49cf9db068ceeba1dd693d714f89ee", 153878787, 153879299);
        Assert.Equal(2, maps.Length);
        var first = maps.Single(m => (int)m["id"] == 153878787);
        var second = maps.Single(m => (int)m["id"] == 153879299);
        Assert.Equal(153879299, first["rightNeighbourId"]);
        Assert.Equal(153878787, second["leftNeighbourId"]);
        foreach (var map in maps)
        {
            var cells = (object[])((Dictionary<string, object>)map["cellsData"])["common:49"];
            Assert.Equal(560, cells.Length);
            Assert.Equal(Enumerable.Range(0, 560), cells.Cast<Dictionary<string, object>>().Select(c => (int)c["cellNumber"]).Order());
            var cell = cells.Cast<Dictionary<string, object>>().Single(c => (int)c["cellNumber"] == 362);
            Assert.Equal((byte)1, cell["mov"]);
            Assert.Equal((byte)1, cell["visible"]);
        }
    }
}
