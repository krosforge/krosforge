using System.IO.Pipes;
using Krosforge.Contracts;
namespace Krosforge.Runtime;
public static class RuntimeEntry
{
    public static async Task<int> RunAsync(string[] args)
    {
        var directory = AppPaths.Data;
        if (args.Length == 2 && args[0] == "--data")
            directory = Path.GetFullPath(args[1]);
        else if (args.Length != 0)
            return 2;
        SafePaths.CheckTree(directory);
        Directory.CreateDirectory(directory);
        FileStream ownership;
        try
        {
            ownership = new FileStream(Path.Combine(directory, "runtime.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return 0; }
        using (ownership)
        using (var service = new RuntimeService(directory))
        {
            while (true)
            {
                await using var pipe = new NamedPipeServerStream(AppPaths.Pipe(directory), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                try
                {
                    await pipe.WaitForConnectionAsync(lifetime.Token);
                    while (pipe.IsConnected)
                    {
                        lifetime.CancelAfter(TimeSpan.FromMinutes(5));
                        var request = await Wire.ReadAsync<Request>(pipe, lifetime.Token);
                        if (request.Version == Wire.Version && request.Operation == "shutdown")
                        {
                            await Wire.WriteAsync(pipe, Response.Ok(true), lifetime.Token);
                            return 0;
                        }
                        await Wire.WriteAsync(pipe, service.Handle(request), lifetime.Token);
                    }
                }
                catch (OperationCanceledException) { return 0; }
                catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
            }
        }
    }
}
