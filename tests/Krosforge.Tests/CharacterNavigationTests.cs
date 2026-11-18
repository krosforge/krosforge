using System.Security.Cryptography;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed class CharacterNavigationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "navigation-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] Original = [10, 20, 30, 40, 50, 60];
    private static readonly byte[] Patched = [10, 20, 31, 41, 50, 60];
    private static readonly CharacterNavigation.Patch[] Changes = [new(2, "1e28", "1f29")];
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private string FilePath
    {
        get { Directory.CreateDirectory(root); return Path.Combine(root, "native.bin"); }
    }
    private Task Apply(CancellationToken token, Action? beforePromotion = null) =>
        CharacterNavigation.ApplyValidatedAsync(FilePath, Hash(Original), Hash(Patched), Changes, token, beforePromotion);

    [Fact]
    public async Task UpgradeIsIdempotentAndPreservesUnrelatedBytes()
    {
        await File.WriteAllBytesAsync(FilePath, Original, TestContext.Current.CancellationToken);
        await Apply(TestContext.Current.CancellationToken);
        Assert.Equal(Patched, await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken));
        await Apply(TestContext.Current.CancellationToken, beforePromotion: () => throw new Exception("Already applied files must not be rewritten."));
        Assert.Single(Directory.GetFiles(root));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedPreparationLeavesOriginalUsableAndRetryable(bool cancel)
    {
        await File.WriteAllBytesAsync(FilePath, Original, TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Action interrupt = () =>
        {
            Assert.Equal(Original, File.ReadAllBytes(FilePath));
            if (cancel) cancellation.Cancel(); else throw new IOException("Simulated preparation failure");
        };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Apply(cancellation.Token, interrupt));
        else await Assert.ThrowsAsync<IOException>(() => Apply(cancellation.Token, interrupt));
        Assert.Equal(Original, await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(root));
        await Apply(TestContext.Current.CancellationToken);
        Assert.Equal(Patched, await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task UnknownOrPartiallyPatchedInputIsNeverChanged()
    {
        byte[] partial = [10, 20, 31, 40, 50, 60];
        await File.WriteAllBytesAsync(FilePath, partial, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => Apply(TestContext.Current.CancellationToken));
        Assert.Equal(partial, await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(root));
    }
    [Fact]
    public async Task IncorrectPatchOrOutputDigestDoesNotPromote()
    {
        await File.WriteAllBytesAsync(FilePath, Original, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IOException>(() => CharacterNavigation.ApplyValidatedAsync(FilePath,
            Hash(Original), Hash(Patched), [new(2, "ffff", "1f29")], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => CharacterNavigation.ApplyValidatedAsync(FilePath,
            Hash(Original), Hash([1]), Changes, TestContext.Current.CancellationToken));
        Assert.Equal(Original, await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(root));
    }
    [Fact]
    public async Task SourceCannotBeUsedAsDestination()
    {
        await Assert.ThrowsAsync<IOException>(() => CharacterNavigation.ApplyAsync(root, root, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => CharacterNavigation.ApplyAsync(root, Path.Combine(root, "child"), TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task LockedWindowsDestinationLeavesOriginalAndCanRetry()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Requires Windows file-sharing semantics.");
        await File.WriteAllBytesAsync(FilePath, Original, TestContext.Current.CancellationToken);
        using (var locked = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = await Record.ExceptionAsync(() => Apply(TestContext.Current.CancellationToken));
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(Original, await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(root));
        await Apply(TestContext.Current.CancellationToken);
        Assert.Equal(Patched, await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task ReferenceAssemblyMatchesTheDocumentedPatchDigest()
    {
        var installation = Environment.GetEnvironmentVariable("KROSFORGE_TEST_CLIENT");
        Assert.SkipWhen(installation is null, "Requires the separately installed reference client; no game assets in CI.");
        Directory.CreateDirectory(root);
        File.Copy(Path.Combine(installation!, "GameAssembly.dll"), FilePath);
        await CharacterNavigation.ApplyValidatedAsync(FilePath, ClientCompatibility.Hashes["GameAssembly.dll"],
            CharacterNavigation.PatchedHash, CharacterNavigation.Patches, TestContext.Current.CancellationToken);
        Assert.Equal(CharacterNavigation.PatchedHash, Hash(await File.ReadAllBytesAsync(FilePath, TestContext.Current.CancellationToken)));
        using var original = File.OpenRead(Path.Combine(installation!, "GameAssembly.dll"));
        Assert.Equal(ClientCompatibility.Hashes["GameAssembly.dll"], Convert.ToHexStringLower(SHA256.HashData(original)));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
