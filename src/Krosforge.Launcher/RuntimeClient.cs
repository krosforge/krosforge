using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Krosforge.Contracts;
namespace Krosforge.Launcher;

public interface IRuntimeConnection : IAsyncDisposable
{
    Task<T> CallAsync<T>(string operation, Guid? id = null, string? value = null, string? confirmation = null);
}
public sealed class RuntimeClient : IRuntimeConnection
{
    private NamedPipeClientStream? pipe;
    private readonly SemaphoreSlim gate = new(1);
    public async Task ConnectAsync()
    {
        if (await TryConnectAsync(250))
            return;
        var executable = Environment.ProcessPath ?? throw new IOException("Não foi possível localizar o aplicativo.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        start.ArgumentList.Add("--runtime");
        start.ArgumentList.Add("--data");
        start.ArgumentList.Add(AppPaths.Data);
        using var process = Process.Start(start);
        if (!await TryConnectAsync(15000))
            throw new IOException("Não foi possível iniciar o runtime local.");
    }
    private async Task<bool> TryConnectAsync(int timeout)
    {
        var candidate = new NamedPipeClientStream(".", AppPaths.Pipe(AppPaths.Data), PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            await candidate.ConnectAsync(timeout);
            pipe = candidate;
            return true;
        }
        catch (TimeoutException) { candidate.Dispose(); return false; }
    }
    public async Task<T> CallAsync<T>(string operation, Guid? id = null, string? value = null, string? confirmation = null)
    {
        await gate.WaitAsync();
        try
        {
            if (pipe is null || !pipe.IsConnected)
            {
                if (operation == "shutdown") return JsonSerializer.Deserialize<T>("true")!;
                await ConnectAsync();
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Wire.WriteAsync(pipe!, new Request(Wire.Version, operation, id, value, confirmation), timeout.Token);
            var result = await Wire.ReadAsync<Response>(pipe!, timeout.Token);
            if (!result.Success)
                throw new InvalidOperationException(result.Error);
            return result.Data!.Value.Deserialize<T>()!;
        }
        catch (IOException) { pipe?.Dispose(); pipe = null; throw; }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (pipe is not null)
            await pipe.DisposeAsync();
        gate.Dispose();
    }
}
