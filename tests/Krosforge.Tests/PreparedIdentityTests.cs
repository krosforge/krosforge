using System.Text;
using Krosforge.Runtime;
using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed class PreparedIdentityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "prepared-identity-" + Guid.NewGuid().ToString("N"));
    [Theory]
    [InlineData("")]
    [InlineData("Vendor\n")]
    [InlineData("Vendor\nTitle\nExtra\n")]
    public void MalformedOriginalIdentityIsARecoverableLaunchError(string identity)
    {
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "Dofus_Data"));
        File.WriteAllText(Path.Combine(source, "Dofus_Data", "app.info"), identity);
        var error = Assert.Throws<IOException>(() => ClientLaunchManager.ValidatePreparedIdentity(source, Path.Combine(root, "target"), new(2, "synthetic", "K12345", "S0001")));
        Assert.Equal("ErrorClientIdentity", error.Message);
    }
    [Fact]
    public void PreparedSettingsMustStillMatchThePatchedOriginal()
    {
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(Path.Combine(source, "Dofus_Data"));
        Directory.CreateDirectory(Path.Combine(target, "Dofus_Data"));
        File.WriteAllText(Path.Combine(source, "Dofus_Data", "app.info"), "Vendor\nTitle\n");
        File.WriteAllBytes(Path.Combine(source, "Dofus_Data", "globalgamemanagers"), Serialized("Vendor").Concat(Serialized("Title")).ToArray());
        var output = Path.Combine(target, "Dofus_Data", "globalgamemanagers");
        File.WriteAllBytes(output, Serialized("K12345").Concat(Serialized("S0001")).ToArray());
        var marker = new PreparedClient(2, "synthetic", "K12345", "S0001");
        ClientLaunchManager.ValidatePreparedIdentity(source, target, marker);
        File.AppendAllText(output, "changed");
        Assert.Equal("ErrorCopyInvalid", Assert.Throws<IOException>(() => ClientLaunchManager.ValidatePreparedIdentity(source, target, marker)).Message);
    }
    private static byte[] Serialized(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return BitConverter.GetBytes(bytes.Length).Concat(bytes).Concat(new byte[(4 - bytes.Length % 4) % 4]).ToArray();
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
