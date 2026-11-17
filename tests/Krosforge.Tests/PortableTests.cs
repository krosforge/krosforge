using Krosforge.Contracts;
using Xunit;
namespace Krosforge.Tests;
public sealed class PortableTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "krosforge-paths-" + Guid.NewGuid().ToString("N"));
    [Fact] public void InstalledApplicationRetainsExistingDataLocation()
    {
        Directory.CreateDirectory(root);
        Assert.Equal(Path.Combine(root, "user", "KrosforgeClient"), AppPaths.ResolveData(root, Path.Combine(root, "user"), installed: true));
    }
    [Fact] public void PortableDefaultUsesSiblingDataAndSurvivesMovingFolder()
    {
        Directory.CreateDirectory(root);
        Assert.Equal(Path.Combine(root, "Krosforge-data"), AppPaths.ResolveData(root, "unused"));
        var moved = root + "-moved";
        Directory.Move(root, moved);
        try { Assert.Equal(Path.Combine(moved, "Krosforge-data"), AppPaths.ResolveData(moved, "unused")); }
        finally { Directory.Move(moved, root); }
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
