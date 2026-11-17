using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Krosforge.Launcher.Updates;
using Xunit;
namespace Krosforge.Tests;

public sealed class UpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "updates-test-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Asset = "Krosforge-0.0.2-win-x64-portable.zip";
    private const string Prefix = "https://github.com/krosforge/krosforge/releases/download/v0.0.2/";
    public UpdateTests() => Directory.CreateDirectory(root);
    [Theory]
    [InlineData("0.0.9", "0.0.10")]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("0.9.9", "1.0.0")]
    public void VersionsUseSemanticOrdering(string earlier, string later) => Assert.True(ReleaseVersion.Parse(earlier).CompareTo(ReleaseVersion.Parse(later)) < 0);
    [Theory]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0/../../bad")]
    public void InvalidVersionsAreRejected(string version) => Assert.Throws<InvalidDataException>(() => ReleaseVersion.Parse(version));
    [Fact]
    public async Task NewerStableReleaseSelectsOnlyExactPortableAsset()
    {
        using var http = Http(_ => JsonResponse(Metadata()));
        var release = await new ReleaseClient(http).CheckAsync(ReleaseVersion.Parse("0.0.1"), Token);
        Assert.Equal(Asset, release!.AssetName);
        Assert.Equal(Prefix + Asset, release.DownloadUrl.AbsoluteUri);
        Assert.Null(await new ReleaseClient(http).CheckAsync(ReleaseVersion.Parse("0.0.2"), Token));
        Assert.Null(await new ReleaseClient(http).CheckAsync(ReleaseVersion.Parse("0.0.3"), Token));
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"draft\":false,\"prerelease\":false,\"tag_name\":2}")]
    [InlineData("{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v0.0.2\",\"assets\":{}}")]
    [InlineData("not-json")]
    public async Task MalformedMetadataFailsAsRecoverableIoError(string json)
    {
        using var http = Http(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReleaseClient(http).CheckAsync(ReleaseVersion.Parse("0.0.1"), Token));
    }
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DraftsAndPrereleasesAreNotOffered(bool draft, bool prerelease)
    {
        using var http = Http(_ => JsonResponse(new { draft, prerelease }));
        Assert.Null(await new ReleaseClient(http).CheckAsync(ReleaseVersion.Parse("0.0.1"), Token));
    }
    [Fact]
    public async Task NullAssetUrlIsReportedAsIoError()
    {
        using var http = Http(_ => JsonResponse(new
        {
            draft = false, prerelease = false, tag_name = "v0.0.2",
            assets = new[] { new { name = Asset, size = 100L, browser_download_url = (string?)null } }
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReleaseClient(http).CheckAsync(ReleaseVersion.Parse("0.0.1"), Token));
    }
    [Fact]
    public async Task ForeignAssetUrlIsRejected()
    {
        using var http = Http(_ => JsonResponse(Metadata(url: "https://example.com/package.zip")));
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReleaseClient(http).CheckAsync(ReleaseVersion.Parse("0.0.1"), Token));
    }
    [Fact]
    public async Task RedirectToUntrustedHostIsRejectedBeforeRequest()
    {
        var requests = 0;
        using var http = Http(_ => { requests++; return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://localhost/secrets") } }; });
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReleaseClient(http).CheckAsync(ReleaseVersion.Parse("0.0.1"), Token));
        Assert.Equal(1, requests);
    }
    [Fact]
    public async Task DownloadChecksHashAndExtractsOneExecutable()
    {
        var package = Package("Krosforge.exe");
        using var http = DownloadHttp(package, Convert.ToHexStringLower(SHA256.HashData(package)));
        var update = new AvailableUpdate(ReleaseVersion.Parse("0.0.2"), Asset, new(Prefix + Asset), new(Prefix + "SHA256SUMS"), package.Length);
        var downloaded = await new ReleaseClient(http).DownloadAsync(update, root, null, Token);
        Assert.Equal(Pe(), File.ReadAllBytes(downloaded.Executable));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Pe())), downloaded.ExecutableHash);
    }
    [Fact]
    public async Task ChecksumFailureLeavesNoExecutableOrPartialPackage()
    {
        var package = Package("Krosforge.exe");
        using var http = DownloadHttp(package, new string('0', 64));
        var update = new AvailableUpdate(ReleaseVersion.Parse("0.0.2"), Asset, new(Prefix + Asset), new(Prefix + "SHA256SUMS"), package.Length);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ReleaseClient(http).DownloadAsync(update, root, null, Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }
    [Theory]
    [InlineData("../Krosforge.exe")]
    [InlineData("folder/Krosforge.exe")]
    [InlineData("Krosforge-data/settings.json")]
    public async Task ArchiveCannotWriteOutsideExactExecutable(string entry)
    {
        var package = Path.Combine(root, "test.zip");
        File.WriteAllBytes(package, Package(entry));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseClient.ExtractExecutableAsync(package, Path.Combine(root, "output.exe"), Token));
        Assert.False(File.Exists(Path.Combine(root, "output.exe")));
    }
    [Fact]
    public async Task InvalidExecutableIsReportedAsIoError()
    {
        var package = Path.Combine(root, "invalid.zip");
        using (var file = File.Create(package))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        using (var content = archive.CreateEntry("Krosforge.exe").Open())
            content.Write(Encoding.UTF8.GetBytes("not a Windows executable"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseClient.ExtractExecutableAsync(package, Path.Combine(root, "output.exe"), Token));
    }
    [Fact]
    public async Task AdditionalArchiveEntriesAreRejected()
    {
        var package = Path.Combine(root, "test.zip");
        File.WriteAllBytes(package, Package("Krosforge.exe", "Krosforge-data/profile.db"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseClient.ExtractExecutableAsync(package, Path.Combine(root, "output.exe"), Token));
    }
    [Fact]
    public async Task ReplacementKeepsBackupAndPreservesUserData()
    {
        var target = Path.Combine(root, "Launcher.exe"); var stage = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage); Directory.CreateDirectory(Path.Combine(root, "Krosforge-data"));
        File.WriteAllText(target, "old"); File.WriteAllText(Path.Combine(stage, "Krosforge.exe"), "new");
        var data = Path.Combine(root, "Krosforge-data", "keep.db"); File.WriteAllText(data, "profile data");
        var plan = new UpdatePlan(target, await ReleaseClient.HashAsync(Path.Combine(stage, "Krosforge.exe"), Token), await ReleaseClient.HashAsync(target, Token), 0, 0, false);
        await UpdateInstaller.ReplaceAsync(plan, stage, Token);
        Assert.Equal("new", File.ReadAllText(target));
        Assert.Equal("old", File.ReadAllText(UpdateInstaller.BackupPath(target, stage)));
        Assert.Equal("profile data", File.ReadAllText(data));
    }
    [Fact]
    public async Task ChangedInstalledExecutableIsNeverOverwritten()
    {
        var target = Path.Combine(root, "Launcher.exe"); var stage = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        File.WriteAllText(target, "changed"); File.WriteAllText(Path.Combine(stage, "Krosforge.exe"), "new");
        var plan = new UpdatePlan(target, await ReleaseClient.HashAsync(Path.Combine(stage, "Krosforge.exe"), Token), new string('0', 64), 0, 0, false);
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.ReplaceAsync(plan, stage, Token));
        Assert.Equal("changed", File.ReadAllText(target));
        Assert.False(File.Exists(UpdateInstaller.BackupPath(target, stage)));
        Assert.Empty(Directory.GetFiles(root, "*.new"));
    }
    [Fact]
    public void CleanupCannotTargetArbitraryDirectory() => Assert.Throws<InvalidDataException>(() => UpdateInstaller.DeleteStaging(root));
    [Fact]
    public async Task RecoveryRestoresVerifiedPreviousExecutable()
    {
        var (plan, backup) = await RecoveryFiles();
        await UpdateInstaller.RestoreAsync(plan, backup, Token);
        Assert.Equal(plan.CurrentHash, await ReleaseClient.HashAsync(plan.Target, Token));
        Assert.False(File.Exists(backup));
    }
    [Fact]
    public async Task RecoveryRejectsChangedBackup()
    {
        var (plan, backup) = await RecoveryFiles();
        File.WriteAllText(backup, "changed backup");
        await Assert.ThrowsAsync<InvalidDataException>(() => UpdateInstaller.RestoreAsync(plan, backup, Token));
        Assert.Equal("failed update", File.ReadAllText(plan.Target));
        Assert.True(File.Exists(backup));
    }
    [Fact]
    public async Task RecoveryRetriesWindowsReadLockUntilReleased()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (plan, backup) = await RecoveryFiles();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var held = new FileStream(plan.Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        var recovery = UpdateInstaller.RestoreAsync(plan, backup, deadline.Token);
        await Task.Delay(3000, Token);
        Assert.False(recovery.IsCompleted);
        held.Dispose();
        await recovery;
        Assert.Equal(plan.CurrentHash, await ReleaseClient.HashAsync(plan.Target, Token));
    }
    [Fact]
    public async Task RecoveryDeadlinePreservesBackupUnderPersistentWindowsLock()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (plan, backup) = await RecoveryFiles();
        using var held = new FileStream(plan.Target, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateInstaller.RestoreAsync(plan, backup, deadline.Token));
        Assert.Equal(plan.CurrentHash, await ReleaseClient.HashAsync(backup, Token));
        Assert.Equal("failed update", File.ReadAllText(plan.Target));
    }
    private async Task<(UpdatePlan Plan, string Backup)> RecoveryFiles()
    {
        var target = Path.Combine(root, "Krosforge.exe");
        var backup = Path.Combine(root, "backup.exe");
        File.WriteAllText(target, "failed update");
        File.WriteAllText(backup, "previous launcher");
        return (new UpdatePlan(target, await ReleaseClient.HashAsync(target, Token), await ReleaseClient.HashAsync(backup, Token), 0, 0, false), backup);
    }
    [Fact]
    public void DuplicateChecksumsAreRejected()
    {
        var line = new string('a', 64) + "  " + Asset + "\n";
        Assert.Throws<InvalidDataException>(() => ReleaseClient.ReadChecksum(line + line, Asset));
    }
    private static object Metadata(long size = 100, string? url = null) => new
    {
        draft = false, prerelease = false, tag_name = "v0.0.2",
        assets = new[] { new { name = Asset, size, browser_download_url = url ?? Prefix + Asset }, new { name = "SHA256SUMS", size = 100L, browser_download_url = Prefix + "SHA256SUMS" }, new { name = "Krosforge-0.0.2-source.zip", size = 100L, browser_download_url = Prefix + "Krosforge-0.0.2-source.zip" } }
    };
    private static HttpClient DownloadHttp(byte[] package, string hash) => Http(request => new HttpResponseMessage(HttpStatusCode.OK)
    { Content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS", StringComparison.Ordinal) ? new StringContent(hash + "  " + Asset + "\n") : new ByteArrayContent(package) });
    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpClient Http(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler(respond));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(respond(request)); }
    }
    private static byte[] Package(params string[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var name in entries) { using var output = archive.CreateEntry(name).Open(); output.Write(Pe()); }
        return memory.ToArray();
    }
    private static byte[] Pe()
    {
        var data = new byte[512]; data[0] = (byte)'M'; data[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x3c), 0x80);
        data[0x80] = (byte)'P'; data[0x81] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x84), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x94), 240);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x96), 0x22);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x98), 0x20b);
        return data;
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
