using Krosforge.Runtime;
using Xunit;
namespace Krosforge.Tests;

public sealed class PreparationTests
{
    [Fact]
    public void ReplacementDoesNotTouchOtherBytes()
    {
        byte[] data = [9, 1, 2, 3, 4, 9];
        ClientPreparation.ReplaceUnique(data, [1, 2, 3, 4], [4, 3, 2, 1]);
        Assert.Equal(new byte[] { 9, 4, 3, 2, 1, 9 }, data);
    }
    [Fact]
    public void AmbiguousIdentityDoesNotPartiallyModifyData()
    {
        byte[] data = [1, 2, 1, 2];
        Assert.Throws<IOException>(() => ClientPreparation.ReplaceUnique(data, [1, 2], [3, 4]));
        Assert.Equal(new byte[] { 1, 2, 1, 2 }, data);
    }
    [Fact]
    public void MissingOrDifferentSizedIdentityIsRejected()
    {
        Assert.Throws<IOException>(() => ClientPreparation.ReplaceUnique([1, 2], [4, 5], [6, 7]));
        Assert.Throws<IOException>(() => ClientPreparation.ReplaceUnique([1, 2], [1, 2], [3]));
    }
    [Fact]
    public void SourceCannotContainOrBeContainedByDestination()
    {
        var root = Path.Combine(Path.GetTempPath(), "source");
        Assert.Throws<IOException>(() => ClientPreparation.EnsureDisjoint(root, root));
        Assert.Throws<IOException>(() => ClientPreparation.EnsureDisjoint(root, Path.Combine(root, "copy")));
        Assert.Throws<IOException>(() => ClientPreparation.EnsureDisjoint(Path.Combine(root, "child"), root));
        ClientPreparation.EnsureDisjoint(root, root + "-copy");
    }
    [Fact]
    public void MerelyClaimingVersionIsNotSufficient()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(root, "Dofus_Data", "StreamingAssets"));
        try
        {
            File.WriteAllText(Path.Combine(root, "Dofus_Data", "StreamingAssets", "version"), "Version=3.7.14.18");
            File.WriteAllText(Path.Combine(root, "Dofus.exe"), "synthetic");
            File.WriteAllText(Path.Combine(root, "GameAssembly.dll"), "synthetic");
            Assert.Throws<IOException>(() => ClientInstallation.Inspect(root));
        }
        finally { Directory.Delete(root, true); }
    }
}
