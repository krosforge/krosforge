using System.Text;
using System.Text.Json;
using Krosforge.Contracts;
using Krosforge.Runtime;
using Xunit;
namespace Krosforge.Tests;

public sealed class PreparationRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "preparation-test-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "source");
    private string Data => Path.Combine(root, "data");
    private string Target => ClientPreparation.Target(Data);
    public PreparationRecoveryTests()
    {
        Directory.CreateDirectory(Path.Combine(Source, "Dofus_Data"));
        File.WriteAllText(Path.Combine(Source, "Dofus_Data", "app.info"), "Vendor\nTitle\n");
        File.WriteAllBytes(Path.Combine(Source, "Dofus_Data", "globalgamemanagers"), Identity());
        File.WriteAllBytes(Path.Combine(Source, "payload.bin"), new byte[256 * 1024]);
    }
    private static byte[] Identity() => Serialize("Vendor").Concat(Serialize("Title")).ToArray();
    private static byte[] Serialize(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return BitConverter.GetBytes(bytes.Length).Concat(bytes).Concat(new byte[(4 - bytes.Length % 4) % 4]).ToArray();
    }
    private static void ValidateFixture(string directory)
    {
        Assert.Equal("Vendor\nTitle\n", File.ReadAllText(Path.Combine(directory, "Dofus_Data", "app.info")));
        Assert.Equal(Identity(), File.ReadAllBytes(Path.Combine(directory, "Dofus_Data", "globalgamemanagers")));
    }
    private Task<string> Prepare(IProgress<CopyProgress>? progress = null, CancellationToken token = default, Action<string>? validate = null) =>
        ClientPreparation.PrepareValidatedAsync(new Installation(Source, "synthetic"), Data, progress, token, validate ?? ValidateFixture);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedCopyCanBeRetriedWithoutLeavingFinalDirectory(bool cancel)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var progress = new InlineProgress(value =>
        {
            Assert.True(value.BytesCopied > 0);
            Assert.False(Directory.Exists(Target));
            if (cancel) cancellation.Cancel(); else throw new IOException("Simulated copy failure");
        });
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Prepare(progress, cancellation.Token));
        else
            await Assert.ThrowsAsync<IOException>(() => Prepare(progress, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Target));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Data, "clients")));
        ValidateFixture(Source);
        Assert.Equal(Target, await Prepare(token: TestContext.Current.CancellationToken));
        var metadata = JsonSerializer.Deserialize<PreparedClient>(await File.ReadAllTextAsync(Path.Combine(Target, "krosforge-client.json"), TestContext.Current.CancellationToken))!;
        Assert.Equal(2, metadata.Layout);
        Assert.Equal(metadata.Company + "\n" + metadata.Product + "\n", File.ReadAllText(Path.Combine(Target, "Dofus_Data", "app.info")));
        ValidateFixture(Source);
    }

    [Fact]
    public async Task ValidationFailureAfterCopyCanBeRetried()
    {
        var validations = 0;
        await Assert.ThrowsAsync<IOException>(() => Prepare(token: TestContext.Current.CancellationToken, validate: directory =>
        {
            Assert.False(Directory.Exists(Target));
            if (++validations == 2) throw new IOException("Simulated validation failure");
            ValidateFixture(directory);
        }));
        Assert.Equal(2, validations);
        Assert.False(Directory.Exists(Target));
        Assert.Empty(Directory.GetDirectories(Path.Combine(Data, "clients")));
        Assert.Equal(Target, await Prepare(token: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExistingCopyIsPreservedWhenPromotionCannotProceed()
    {
        var validations = 0;
        await Assert.ThrowsAsync<IOException>(() => Prepare(token: TestContext.Current.CancellationToken, validate: directory =>
        {
            ValidateFixture(directory);
            if (++validations == 2)
            {
                Directory.CreateDirectory(Target);
                File.WriteAllText(Path.Combine(Target, "keep.txt"), "Existing copy");
            }
        }));
        Assert.Equal("Existing copy", File.ReadAllText(Path.Combine(Target, "keep.txt")));
        Assert.Equal(new[] { Target }, Directory.GetDirectories(Path.Combine(Data, "clients")));
        Assert.False(File.Exists(Path.Combine(Target, "krosforge-client.json")));
        ValidateFixture(Source);
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class InlineProgress(Action<CopyProgress> report) : IProgress<CopyProgress>
    {
        public void Report(CopyProgress value) => report(value);
    }
}
