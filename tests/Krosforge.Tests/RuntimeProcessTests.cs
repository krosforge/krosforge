using System.Diagnostics;
using System.IO.Pipes;
using Krosforge.Contracts;
using Xunit;
namespace Krosforge.Tests;

public sealed class RuntimeProcessTests
{
    [Fact]
    public async Task RuntimeAcceptsCommandsAndPersistsAcrossProcesses()
    {
        var root = Path.Combine(Path.GetTempPath(), "runtime-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var instance = await Instance.Start(root))
            {
                await Wire.WriteAsync(instance.Pipe, new Request(1, "create", Value: "Persistente"), instance.Timeout.Token);
                Assert.True((await Wire.ReadAsync<Response>(instance.Pipe, instance.Timeout.Token)).Success);
            }
            await using (var instance = await Instance.Start(root))
            {
                await Wire.WriteAsync(instance.Pipe, new Request(1, "snapshot"), instance.Timeout.Token);
                var response = await Wire.ReadAsync<Response>(instance.Pipe, instance.Timeout.Token);
                Assert.Equal("Persistente", response.Data!.Value.GetProperty("Profiles")[0].GetProperty("Name").GetString());
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class Instance(Process process, NamedPipeClientStream pipe, CancellationTokenSource timeout) : IAsyncDisposable
    {
        public NamedPipeClientStream Pipe => pipe;
        public CancellationTokenSource Timeout => timeout;
        public static async Task<Instance> Start(string root)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Krosforge.dll"));
            start.ArgumentList.Add("--runtime");
            start.ArgumentList.Add("--data");
            start.ArgumentList.Add(root);
            var process = Process.Start(start)!;
            var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var pipe = new NamedPipeClientStream(".", AppPaths.Pipe(root), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(timeout.Token);
                return new(process, pipe, timeout);
            }
            catch { if (!process.HasExited) process.Kill(); process.Dispose(); pipe.Dispose(); timeout.Dispose(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Wire.WriteAsync(pipe, new Request(1, "shutdown"), timeout.Token);
                Assert.True((await Wire.ReadAsync<Response>(pipe, timeout.Token)).Success);
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(0, process.ExitCode);
            }
            finally { if (!process.HasExited) process.Kill(); await pipe.DisposeAsync(); process.Dispose(); timeout.Dispose(); }
        }
    }
}
