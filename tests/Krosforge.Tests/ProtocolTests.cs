using Krosforge.Contracts;
using Krosforge.Runtime;
using Xunit;
namespace Krosforge.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public async Task FrameRoundTripPreservesUnicodeAndIdentity()
    {
        using var stream = new MemoryStream();
        var request = new Request(1, "create", Guid.NewGuid(), "João");
        await Wire.WriteAsync(stream, request, TestContext.Current.CancellationToken);
        stream.Position = 0;
        Assert.Equal(request, await Wire.ReadAsync<Request>(stream, TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(65537)]
    [InlineData(0)]
    public async Task InvalidFrameLengthsAreRejectedBeforeAllocation(int size)
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(size));
        await Assert.ThrowsAsync<InvalidDataException>(() => Wire.ReadAsync<Request>(stream, TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task TruncatedFrameDoesNotReturnSuccess()
    {
        using var stream = new MemoryStream(new byte[] { 5, 0, 0, 0, 123 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => Wire.ReadAsync<Request>(stream, TestContext.Current.CancellationToken));
    }
    [Fact]
    public void IncorrectVersionCannotMutateProfiles()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            using var service = new RuntimeService(path);
            Assert.False(service.Handle(new(2, "create", Value: "Conta")).Success);
            Assert.Empty(service.Handle(new(1, "snapshot")).Data!.Value.GetProperty("Profiles").EnumerateArray());
        }
        finally { Directory.Delete(path, true); }
    }
}
