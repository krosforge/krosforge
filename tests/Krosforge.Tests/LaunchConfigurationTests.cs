using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed class LaunchConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "launch-config-" + Guid.NewGuid().ToString("N"));
    private string Assets => Path.Combine(root, "client", "Dofus_Data", "StreamingAssets");
    [Fact]
    public async Task ConfigUpdatePreservesVersionAndOtherFields()
    {
        Directory.CreateDirectory(Assets);
        var version = Path.Combine(Assets, "version");
        await File.WriteAllTextAsync(version, "Version=3.7.14.18\nBuildDate=test\nConfigUrl=https://example.invalid/config\n", TestContext.Current.CancellationToken);
        await ClientLaunchConfiguration.WriteAsync(Path.Combine(root, "client"), "http://127.0.0.1:12345", TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "Version=3.7.14.18", "BuildDate=test", "ConfigUrl=http://127.0.0.1:12345/config.json" }, File.ReadAllLines(version));
        Assert.Single(Directory.GetFiles(Assets));
    }
    [Fact]
    public async Task LinkedVersionCannotRewriteOriginalFile()
    {
        Directory.CreateDirectory(Assets);
        var original = Path.Combine(root, "original-version");
        File.WriteAllText(original, "Version=3.7.14.18\nConfigUrl=https://example.invalid/original\n");
        var link = Path.Combine(Assets, "version");
        try { File.CreateSymbolicLink(link, original); }
        catch (UnauthorizedAccessException) { return; }
        await Assert.ThrowsAnyAsync<IOException>(() => ClientLaunchConfiguration.WriteAsync(Path.Combine(root, "client"), "http://127.0.0.1:12345", TestContext.Current.CancellationToken));
        Assert.Contains("https://example.invalid/original", File.ReadAllText(original));
    }
    [Fact]
    public async Task LinkedAssetsDirectoryCannotRewriteOriginalFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Assets)!);
        var original = Path.Combine(root, "original-assets");
        Directory.CreateDirectory(original);
        File.WriteAllText(Path.Combine(original, "version"), "Version=3.7.14.18\nConfigUrl=https://example.invalid/original\n");
        try { Directory.CreateSymbolicLink(Assets, original); }
        catch (UnauthorizedAccessException) { return; }
        await Assert.ThrowsAnyAsync<IOException>(() => ClientLaunchConfiguration.WriteAsync(Path.Combine(root, "client"), "http://127.0.0.1:12345", TestContext.Current.CancellationToken));
        Assert.Contains("https://example.invalid/original", File.ReadAllText(Path.Combine(original, "version")));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
