using Krosforge.Runtime.Native;
using Krosforge.Runtime.Native.Protocol;
using Xunit;
namespace Krosforge.Tests;

public sealed class WorldMovementWireTests
{
    [Fact]
    public void ReadsIndependentPackedAndUnpackedFixtures()
    {
        foreach (var bytes in new[] { "0a0200021064", "080008021064" })
        {
            var move = WorldMovementWire.Read(Convert.FromHexString(bytes));
            Assert.Equal(100, move.Map); Assert.Equal(new[] { 0, 2 }, move.TurningCells); Assert.Equal(0, move.Direction);
        }
        Assert.Equal(200, WorldMovementWire.ChangeMap(Convert.FromHexString("10c801")));
        Assert.Equal(2, WorldMovementWire.Cancellation(Convert.FromHexString("0802")));
    }
    [Theory]
    [InlineData("0a01801064")]
    [InlineData("0a04008080021064")]
    [InlineData("0a02000210641801")]
    [InlineData("0a0200021000")]
    [InlineData("0a01001064")]
    public void RejectsMalformedUnknownOrUnboundedMovement(string payload) =>
        Assert.Throws<ProtocolException>(() => WorldMovementWire.Read(Convert.FromHexString(payload)));
    [Fact]
    public void UnknownAcknowledgementOrTransitionModesAreNotAccepted()
    {
        Assert.Throws<ProtocolException>(() => WorldMovementWire.Acknowledgement([8, 1]));
        Assert.Throws<ProtocolException>(() => WorldMovementWire.ChangeMap([8, 1, 16, 100]));
        Assert.Throws<ProtocolException>(() => WorldMovementWire.Cancellation(Convert.FromHexString("08b004")));
    }
}
