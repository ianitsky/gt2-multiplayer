namespace GT2Port.Multiplayer;

/// <summary>
/// The session that sets the grid before the race.
///
/// A room with qualifying runs one of these first: two laps, everybody out at
/// once, and the grid for the race afterwards is the order of their best laps.
/// Then the lobby comes back and waits for the host to start the race itself.
///
/// Almost nothing here is new machinery, which is the point. A qualifying
/// session is a race - the same launch, the same start barrier, the same
/// return to the lobby, the same exchange of results afterwards - differing in
/// three things: it is always two laps, it is scored on the best lap rather
/// than on laps and total time, and when it ends the room moves on to
/// arranging the race instead of staying where it was.
/// </summary>
public static class Qualifying
{
    /// <summary>
    /// How long a qualifying session is, and it is not the host's to change.
    ///
    /// Two laps: one from a standing start, which is nobody's best, and one
    /// flying. A room's own lap count and clock are what the *race* is, and
    /// qualifying borrowing them would mean a three-hour qualifying session
    /// could be asked for by accident.
    /// </summary>
    public const byte Laps = 2;

    /// <summary>What to call the session the lobby is about to start.</summary>
    public static string Title(Room room) => room.QualifyingNext ? "Qualifying" : "Race";

    /// <summary>What the button that starts it should say.</summary>
    public static string StartSays(Room room) =>
        room.QualifyingNext ? "Start qualifying" : "Start race";

    /// <summary>What the table of results is a table of.</summary>
    public static string ResultsAre(bool qualifying) =>
        qualifying ? "Qualifying" : "Last race";
}
