using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Krosforge.Contracts;
using Krosforge.Launcher;
using Xunit;
namespace Krosforge.Tests;

public sealed class RuntimeClientTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutDiscardsLateOrPartialResponseBeforeNextRequest(bool partial)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pipe-test-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var first = Server(directory);
        await using var client = new RuntimeClient(directory, TimeSpan.FromSeconds(1));
        var snapshot = client.CallAsync<Snapshot>("snapshot");
        await first.WaitForConnectionAsync(deadline.Token);
        Assert.Equal("snapshot", (await Wire.ReadAsync<Request>(first, deadline.Token)).Operation);
        var data = JsonSerializer.SerializeToUtf8Bytes(Response.Ok(new Snapshot([], null)));
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        if (partial)
        {
            await first.WriteAsync(header, deadline.Token);
            await first.WriteAsync(data.AsMemory(0, data.Length / 2), deadline.Token);
            await first.FlushAsync(deadline.Token);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => snapshot);
        // Keep the old server connection alive: a reused pipe could consume this stale response.
        try
        {
            if (!partial) await first.WriteAsync(header, deadline.Token);
            await first.WriteAsync(data.AsMemory(partial ? data.Length / 2 : 0), deadline.Token);
            await first.FlushAsync(deadline.Token);
        }
        catch (IOException) { }
        await using var second = Server(directory);
        var reply = ReplyToPing(second, deadline.Token);
        var ping = client.CallAsync<bool>("ping");
        await Task.WhenAll(reply, ping).WaitAsync(deadline.Token);
        Assert.True(await ping);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"Success\":true,\"Data\":{\"Profiles\":[]}}")]
    [InlineData("{\"Success\":true,\"Data\":null}")]
    public async Task InvalidResponseIsReportedAsIoErrorAndConnectionIsReplaced(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pipe-test-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var first = Server(directory);
        await using var client = new RuntimeClient(directory, TimeSpan.FromSeconds(3));
        var call = client.CallAsync<bool>("ping");
        await first.WaitForConnectionAsync(deadline.Token);
        await Wire.ReadAsync<Request>(first, deadline.Token);
        var data = Encoding.UTF8.GetBytes(json);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await first.WriteAsync(header, deadline.Token);
        await first.WriteAsync(data, deadline.Token);
        await first.FlushAsync(deadline.Token);
        await Assert.ThrowsAsync<IOException>(() => call);
        await using var second = Server(directory);
        var reply = ReplyToPing(second, deadline.Token);
        var ping = client.CallAsync<bool>("ping");
        await Task.WhenAll(reply, ping).WaitAsync(deadline.Token);
        Assert.True(await ping);
    }

    [Fact]
    public async Task ApplicationRejectionKeepsSynchronizedConnectionUsable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pipe-test-" + Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var server = Server(directory);
        await using var client = new RuntimeClient(directory, TimeSpan.FromSeconds(3));
        var call = client.CallAsync<bool>("rename");
        await server.WaitForConnectionAsync(deadline.Token);
        await Wire.ReadAsync<Request>(server, deadline.Token);
        await Wire.WriteAsync(server, Response.Fail("Rejected"), deadline.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => call);
        var ping = client.CallAsync<bool>("ping");
        Assert.Equal("ping", (await Wire.ReadAsync<Request>(server, deadline.Token)).Operation);
        await Wire.WriteAsync(server, Response.Ok(true), deadline.Token);
        Assert.True(await ping);
    }

    private static NamedPipeServerStream Server(string directory) => new(AppPaths.Pipe(directory), PipeDirection.InOut, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    private static async Task ReplyToPing(NamedPipeServerStream server, CancellationToken token)
    {
        await server.WaitForConnectionAsync(token);
        Assert.Equal("ping", (await Wire.ReadAsync<Request>(server, token)).Operation);
        await Wire.WriteAsync(server, Response.Ok(true), token);
    }
}
