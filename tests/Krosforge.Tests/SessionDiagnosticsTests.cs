using Krosforge.Runtime.Native;
using Xunit;
namespace Krosforge.Tests;

public sealed class SessionDiagnosticsTests
{
    [Fact]
    public void LaterMovementIsRetainedAndFilesStayBoundedAfterRotation()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var diagnostics = new SessionDiagnostics(root))
            {
                for (var i = 0; i < 3200; i++) diagnostics.Write($"world-path sequence={i} cells={new string('1', 1300)}");
                diagnostics.Write("world-correction actor=1 cell=305 final-marker");
            }
            var files = Directory.GetFiles(Path.Combine(root, "logs"));
            Assert.Equal(3, files.Length);
            Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 1024 * 1024));
            var retained = string.Join('\n', files.Select(File.ReadAllText));
            Assert.Contains("sequence=3199", retained);
            Assert.Contains("world-correction actor=1 cell=305 final-marker", retained);
            Assert.DoesNotContain("sequence=0 cells=", retained);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void UnwritableLogDirectoryDoesNotFailSessionOrShutdown()
    {
        var root = Path.Combine(Path.GetTempPath(), "diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "logs"), "existing-file");
            using (var diagnostics = new SessionDiagnostics(root))
                for (var i = 0; i < 5000; i++) diagnostics.Write("world-request jok");
            Assert.Equal("existing-file", File.ReadAllText(Path.Combine(root, "logs")));
        }
        finally { Directory.Delete(root, true); }
    }
}
