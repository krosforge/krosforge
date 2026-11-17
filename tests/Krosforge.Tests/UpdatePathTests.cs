using System.Runtime.InteropServices;
using System.Text;
using Krosforge.Launcher.Updates;
using Xunit;
namespace Krosforge.Tests;

public sealed class UpdatePathTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint capacity);
    [Fact]
    public void WindowsShortNamesReferToTheSameOwnedStagingDirectoryAndExecutable()
    {
        if (!OperatingSystem.IsWindows()) return;
        var stage = UpdateInstaller.NewStagingDirectory();
        try
        {
            var executable = Path.Combine(stage, "Launcher with a long name.exe");
            File.WriteAllText(executable, "test");
            var shortDirectory = ShortPath(stage);
            var shortExecutable = ShortPath(executable);
            Assert.True(UpdatePaths.Same(executable, shortExecutable));
            Assert.Equal(UpdatePaths.Normalize(stage), UpdateInstaller.ValidateDirectory(shortDirectory));
            Assert.Equal(UpdatePaths.Normalize(Path.Combine(stage, "not-created-yet")), UpdatePaths.Normalize(Path.Combine(shortDirectory, "not-created-yet")));
        }
        finally { UpdateInstaller.DeleteStaging(stage); }
    }
    [Fact]
    public void NormalizationStillRejectsStagingOutsideOwnedRoot()
    {
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidDataException>(() => UpdateInstaller.ValidateDirectory(outside));
    }
    private static string ShortPath(string path)
    {
        var buffer = new StringBuilder(32768);
        var length = GetShortPathNameW(path, buffer, (uint)buffer.Capacity);
        Assert.True(length > 0 && length < buffer.Capacity);
        return buffer.ToString();
    }
}
