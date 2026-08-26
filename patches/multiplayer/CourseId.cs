namespace GT2Port.Multiplayer;

/// <summary>
/// The number the game uses for a course, from the course's asset code.
///
/// GT2 hashes the code with a rotate-and-add at 0x80083004:
///
///     h = 0; for each byte: h = rotl(h, 6) + byte
///
/// That is where the numbers in the roster at 0x801E18E0 come from, which is
/// why none of them appear in any overlay image and why crc32, djb2, sdbm and
/// the fnv pair all miss. Every id the game listed checks out against the
/// codes CourseTable already carries - tahiti_t is 0x6AD87E5E, tahiti_t_2p is
/// 0xF97FA851, 2p_sprint2 is 0x809C807E.
///
/// Having it means the roster is a diagnostic rather than a dependency: a room
/// names a course by asset code, and the code is enough. Matching by display
/// name never could be, since the disc has three courses called Tahiti Road.
/// </summary>
public static class CourseId
{
    /// <summary>How far the game rotates before adding each byte.</summary>
    const int Rotate = 6;

    public static uint Of(string code)
    {
        uint h = 0u;
        foreach (char c in code)
        {
            h = (h << Rotate) | (h >> (32 - Rotate));
            h += (byte)c;
        }
        return h;
    }
}
