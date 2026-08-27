using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Asks the game, in one race, what makes an entrant answer to a controller.
///
/// Reading has settled everything around this and not this. The input path is
/// known end to end - two raw buffers at 0x801F0C98 and 0x801F0CBA, a VBlank
/// callback that decodes each into a reader record, and the race registering
/// its own pair at 0x800A9528 and 0x800A95D8 on its first frame. What is not
/// known is what turns a decoded record into a car's steering, and static
/// reading has run out twice looking for it: the offsets that look right are
/// reached through a register the caller supplies, so they cannot be tied to
/// the race's object without running the game.
///
/// The game has exactly one flag that separates a car it drives from a car a
/// person drives - IsAi, at entrant +0x82, which RaceGrid already writes. So
/// rather than hunt for the AI, this clears it on the second entrant and feeds
/// the second pad a held accelerator. Three things can happen, and each says
/// what to build next:
///
///   the car pulls away    - the game drives any non-AI entrant from the pad of
///                           the same index, and a LAN race is remote input
///                           written into 0x801F0CBA
///   the car sits still    - IsAi takes the AI off it but nothing puts a driver
///                           on, and the human count from the mode byte is the
///                           next lever
///   the car races anyway  - IsAi is not the selector, and the search goes on
///
/// Off unless GT2_SECOND_HUMAN is set, and only ever inside a launched race.
/// </summary>
public static class SecondDriver
{
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("GT2_SECOND_HUMAN") is not (null or "");

    /// <summary>The race block, and the entrant this experiment hands over.</summary>
    const uint Block = 0x801D585Cu;
    const int FirstEntrant = 0x5C;
    const int EntrantSize = 0xD0;
    const int AiSkill = 0x42;
    const int IsAi = 0x82;
    const int SecondEntrant = 1;

    /// <summary>The raw eight bytes the decoder reads pad 1 out of.</summary>
    const uint PadOneRaw = 0x801F0CBAu;

    /// <summary>What the decoder expects to find there.</summary>
    const byte Present = 0;            // the connection byte is zero when a pad is there
    const byte DigitalPad = 0x41;      // the type pad 0 reports, high nibble 4
    const byte StickCentre = 0x80;

    /// <summary>
    /// The buttons to hold, active low the way the hardware reports them.
    /// Cross is bit 14, and cross is what an arcade race accelerates with.
    /// </summary>
    const ushort NothingPressed = 0xFFFF;
    const ushort Cross = 0x4000;

    static bool _said;

    /// <summary>Takes the AI off the second entrant, once the race is built.</summary>
    public static void HandTheSecondCarOver(IMemory m)
    {
        if (!Enabled) return;

        uint entrant = Block + (uint)(FirstEntrant + SecondEntrant * EntrantSize);
        m.WriteU8(entrant + IsAi, 0);
        m.WriteU8(entrant + AiSkill, 0);

        Console.Error.WriteLine(
            $"[second] entrant {SecondEntrant} at 0x{entrant:X8} is marked as a person's car");
    }

    /// <summary>
    /// Holds the accelerator on pad 1, every race frame.
    ///
    /// Written straight into the raw buffer rather than into the reader record:
    /// the record is what the decoder fills, so writing there would be undone
    /// on the next VBlank. The buffer is what the decoder reads.
    /// </summary>
    public static void DrivePadOne(IMemory m)
    {
        if (!Enabled) return;
        Write(m);

        if (_said) return;
        _said = true;
        Console.Error.WriteLine(
            $"[second] pad 1 at 0x{PadOneRaw:X8} is holding cross");
    }

    /// <summary>
    /// The bytes themselves, separated from whether the experiment is on so a
    /// test can check them. A test that returns early when a switch is off
    /// asserts nothing, which is worse than not having one.
    /// </summary>
    internal static void Write(IMemory m)
    {
        m.WriteU8(PadOneRaw + 0u, Present);
        m.WriteU8(PadOneRaw + 1u, DigitalPad);

        ushort buttons = NothingPressed & unchecked((ushort)~Cross);
        m.WriteU8(PadOneRaw + 2u, (byte)(buttons & 0xFF));
        m.WriteU8(PadOneRaw + 3u, (byte)(buttons >> 8));

        for (uint i = 4; i < 8; i++) m.WriteU8(PadOneRaw + i, StickCentre);
    }
}
