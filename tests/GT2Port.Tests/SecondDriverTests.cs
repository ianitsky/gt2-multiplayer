using GT2Port.Multiplayer;
using RecompOne.Runtime.Memory;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// The pad reports buttons active low - a run with nothing held read 0xFFFF -
/// so holding one means clearing its bit, and getting that backwards would
/// hold every button but the one meant.
/// </summary>
public class SecondDriverTests
{
    const uint PadOneRaw = 0x801F0CBAu;

    [Fact]
    public void HoldsCrossAndNothingElseOnTheSecondPad()
    {
        var m = new PSMemory();
        SecondDriver.Write(m);

        ushort buttons = (ushort)(m.ReadU8(PadOneRaw + 3u) << 8 | m.ReadU8(PadOneRaw + 2u));

        Assert.Equal(0xBFFF, buttons);                       // every bit set but cross
        Assert.Equal(0, m.ReadU8(PadOneRaw));                // the pad reads as present
        Assert.Equal(0x41, m.ReadU8(PadOneRaw + 1u));        // and as a digital pad
        for (uint i = 4; i < 8; i++)
            Assert.Equal(0x80, m.ReadU8(PadOneRaw + i));     // sticks centred
    }
}
