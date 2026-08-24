using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port;

/// <summary>
/// Skips the Combined Disc's opening video.
///
/// That disc boots into gt2_06, whose entry point is only two steps: play the
/// arcade intro, then load overlay 1 - the front-end that offers Arcade and GT
/// mode. The intro streams from STREAM.DAT through a decode path this port has
/// never implemented, so it reads sectors and polls callbacks forever and the
/// screen stays black.
///
/// Standing in for the player leaves the second step intact, which is the one
/// that matters. It costs the intro, which nobody watches twice.
/// </summary>
public static class SkipIntro
{
    public static void PlayNothing(CpuContext c, IMemory m)
    {
        Console.WriteLine("[SkipIntro] the opening video was not played");
    }
}
