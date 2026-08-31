namespace GT2Port.Multiplayer;

/// <summary>
/// Sends the end of a race back to the lobby rather than to the arcade menu.
///
/// A race is an overlay. The arcade loads gt2_01 over itself to run one, and
/// when the race is finished gt2_01 is gone, so something has to be loaded back
/// in its place - and every overlay load in this game goes through
/// gt2_load_overlay, which this port already hooks. So the end of a race is not
/// a moment that has to be hunted for. It is the next overlay to arrive after
/// the race's own.
///
/// That also means this needs nothing from the race itself: no finish line, no
/// results screen, no phase number. A race that ends because it was won and one
/// that ends because the player quit both end by loading something else.
///
/// The room outlives the race on purpose - the session socket is kept when a
/// lobby ends in a race rather than dropped, and every player is still in the
/// Room object they left - so coming back is reopening the panel over it. What
/// does have to be undone is this port's own memory of the race just run: every
/// class that does something once, on the first frame or the first grid or the
/// first time the room is held at the line, would otherwise say it has already
/// happened and let the second race start with none of it.
/// </summary>
public static class BackToTheLobby
{
    /// <summary>gt2_01's entry point - the overlay that runs a race.</summary>
    const uint RaceOverlay = 0x80011F64u;

    /// <summary>gt2_03's - the arcade, and so what a finished race comes back to.</summary>
    const uint Arcade = 0x80011750u;

    /// <summary>
    /// Off with GT2_STAY_IN_THE_ARCADE, which leaves a finished race ending
    /// where it always did.
    /// </summary>
    static readonly bool Wanted =
        Environment.GetEnvironmentVariable("GT2_STAY_IN_THE_ARCADE") is (null or "");

    static bool _racing;

    /// <summary>What an arriving overlay means for a race.</summary>
    internal enum Arrival
    {
        /// <summary>No race is running and none is starting - nothing to do.</summary>
        Nothing,

        /// <summary>The race overlay itself: a race is about to run.</summary>
        ARaceIsStarting,

        /// <summary>The arcade, after a race: the room is waiting behind it.</summary>
        TheRaceIsOverAndTheRoomIsThere,

        /// <summary>A race ended into something that is not the arcade.</summary>
        TheRaceIsOverSomewhereElse,
    }

    /// <summary>
    /// What an arriving overlay means, given whether a race is running.
    ///
    /// Pulled out of <see cref="OverlayArriving"/> because that runs a whole
    /// lobby, which needs a live window and cannot be tested; this is
    /// everything about the decision that does not. The rule worth pinning is
    /// that only the *first* overlay after the race counts as the end of one -
    /// the arcade loads several more while it sets a race up, and each of those
    /// would otherwise read as another race ending.
    /// </summary>
    internal static Arrival WhatItMeans(bool racing, uint entry)
    {
        if (entry == RaceOverlay) return Arrival.ARaceIsStarting;
        if (!racing) return Arrival.Nothing;
        return entry == Arcade
            ? Arrival.TheRaceIsOverAndTheRoomIsThere
            : Arrival.TheRaceIsOverSomewhereElse;
    }

    /// <summary>
    /// Called for every overlay load, before it happens - so this either does
    /// nothing, or runs a whole lobby and agrees the next race before the
    /// arcade it is standing in front of has loaded a byte.
    /// </summary>
    public static void OverlayArriving(uint entry)
    {
        var means = WhatItMeans(_racing, entry);
        _racing = means == Arrival.ARaceIsStarting;

        if (means is Arrival.Nothing or Arrival.ARaceIsStarting) return;

        // Printed whatever follows, and whether or not this acts on it: if the
        // arcade is not what comes back after a race, this line is what names
        // what does.
        Console.Error.WriteLine(
            $"[lobby] the race overlay is gone - 0x{entry:X8} is what follows it");

        if (!Wanted || means != Arrival.TheRaceIsOverAndTheRoomIsThere) return;

        // A race with no room behind it - the arcade's own, or one launched
        // from a capture - has nothing to go back to.
        if (!ModeHook.HasARoomToReturnTo)
        {
            Console.Error.WriteLine("[lobby] no room to go back to - the arcade keeps it");
            return;
        }

        ForgetTheRaceJustRun();
        ModeHook.ReturnToTheLobby();
    }

    /// <summary>
    /// Puts every "this has already happened" back to "it has not".
    ///
    /// Each of these is a class that does something exactly once per race, and
    /// each one would be silently wrong rather than loudly broken on a second:
    /// the start barrier would never be reached, so machines would begin
    /// whenever they finished loading; the grid would keep the last race's
    /// rotation; a viewer's second race would come up as an ordinary one.
    /// </summary>
    static void ForgetTheRaceJustRun()
    {
        DirectRace.Forget();
        RaceStartLine.Forget();
        RacePhases.Forget();
        CarSync.Forget();
        ReplayView.Forget();
    }
}
