using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Starts a race from the lobby without the player walking the arcade's menus.
///
/// Everything tried here that reached past the arcade and did the work itself
/// has failed, each time on a subsystem left in a state the game never puts it
/// in. What works is the opposite: let the arcade do all of it, and change only
/// two answers.
///
///   - The arcade's first screen is a loop that asks slot 0x24 of the screen's
///     vtable every pass whether to keep going. That same method is what ticks
///     the car loader, so the game loads the room's car through its own frame
///     loop. Once the car is in memory the answer becomes zero, and the loop
///     ends the way it ends for a player: its own last pass, its own teardown,
///     back into the arcade's race case.
///
///   - That race case builds a 720-byte parameter block describing the race.
///     Built without the menus it comes out all zeroes, since it is assembled
///     from what the screens decided - the course is in it as plain text. So a
///     captured one is written over the top after the game builds it.
///
/// Everything else - the seeds, the pre-race screen, the copy, gt2_04, the race
/// overlay - is the arcade's own, untouched.
/// </summary>
public static class DirectRace
{
    /// <summary>
    /// What the arcade's own menu answers when the player starts a race.
    ///
    /// The screen's step method asks the current page what the player wants:
    /// it reads the page's table at [screen+0x1D0 + depth*4], calls slot 1, and
    /// uses the answer to index the table at 0x80026F98. Seven answers, and
    /// they are the whole vocabulary of the arcade menus:
    ///
    ///   0  stay on this page          4  leave, exit byte 0
    ///   1  go into a sub-page         5  leave, exit byte 2
    ///   2  pop back a page            6  leave, exit byte 3
    ///   3  leave, exit byte 1 - the race
    ///
    /// Answer 3 ends the same way this does: it writes the exit byte and sets
    /// the loop's result to zero, and the rest of the step method runs either
    /// way. Giving the answer itself would be the tidier telling, but the page
    /// that owns it changes - the arcade pushes the menu at 0x80052214 within a
    /// frame or two of starting, and slot 1 moves with it - so there is no one
    /// function to answer from. Writing the byte and the result is the same end
    /// state reached without having to know which page is on top.
    /// </summary>
    const uint ExitByte = 0x801EF5F4u;

    /// <summary>The exit that is the race, read out of the table at 0x800267DC.</summary>
    const byte TheRace = 1;

    /// <summary>
    /// The arcade's own "stop the music", on the object at 0x800F3A20.
    ///
    /// The menus are a stack of pages, and popping one calls the page's slot 0
    /// with 1, which is func_8001D568, which calls this. A player reaches the
    /// race through pages, so by the time they start one the music has already
    /// been stopped by a pop. A launch that walks no pages never stops it, and
    /// the sequencer then keeps stepping a stream that gt2_01 has landed on
    /// top of - which is a byte read from 0xFE469CAA inside a VBlank.
    /// </summary>
    const uint StopTheArcadeMusic = 0x80025878u;

    /// <summary>Where the arcade builds its race parameters, and how many.</summary>
    const uint Parameters = 0x801C3350u;
    const int ParametersSize = 0x2D0;

    static readonly string ParametersPath = Path.Combine("config", "race-params.bin");

    /// <summary>How long to let the car load before starting anyway.</summary>
    static readonly TimeSpan CarPatience = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Off unless GT2_DIRECT_LAUNCH is set. It is still untried against a path
    /// that works.
    /// </summary>
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("GT2_DIRECT_LAUNCH") is not (null or "");

    /// <summary>Whether this ended the arcade's first screen rather than the player.</summary>
    public static bool EndedTheScreen { get; private set; }

    static bool _armed;
    static DateTime _armedAt;
    static bool _endNow;
    static byte _step;
    static DateTime _lastSaid;
    static bool _said;

    /// <summary>What the lobby settled, kept because the race has to be described.</summary>
    /// <summary>
    /// What the lobby settled, kept because the race has to be described.
    ///
    /// <paramref name="Players"/> is the drivers, not the room - a viewer is in
    /// the room and not in the race, and the seat every pose is keyed by counts
    /// along this list. <paramref name="Me"/> is the driver the race is built
    /// around: this machine's own player when it is racing, and the one it is
    /// watching when it is not, so a viewer's race is the race that driver's
    /// own machine builds.
    /// </summary>
    public sealed record Pending(
        IReadOnlyList<Player> Players, string Me, string Car, string Course, CarCatalogue? Cars,
        bool Watching = false, byte Laps = RaceLaps.AsBuilt);

    static Pending? _race;

    /// <summary>The race the lobby agreed, while one is running.</summary>
    public static Pending? Racing => _race;

    /// <summary>
    /// Forgets the race just run, so the next one is set up from nothing.
    ///
    /// <see cref="Expect"/> rearms most of this, but not the flags that say a
    /// screen has already been wound down - and a second race walks through the
    /// same two arcade screens the first one did. Left standing, they end those
    /// screens before they have started.
    /// </summary>
    public static void Forget()
    {
        _race = null;
        _armed = false;
        _endNow = false;
        _step = 0;
        _said = false;
        _preRaceEnded = false;
        EndedTheScreen = false;
    }

    /// <summary>Called by the lobby when a race has been agreed and is to start.</summary>
    public static void Expect(Pending race)
    {
        if (!Enabled)
        {
            Console.Error.WriteLine("[direct] a race is ready but GT2_DIRECT_LAUNCH is not set - using the menus");
            return;
        }

        _race = race;
        _armed = true;
        _armedAt = DateTime.UtcNow;
        _said = false;
        Console.Error.WriteLine(
            $"[direct] a race is waiting: {race.Players.Count} driver(s),"
            + (race.Watching ? $" watching {race.Me} in {race.Car}" : $" {race.Me} in {race.Car}"));

        // Here rather than at the arcade's entry point, which is a frame or
        // two later: this runs while the lobby's overlay hook is still on the
        // stack, so the arcade never gets a frame on screen at all.
        ArcadeCurtain.Raise($"{race.Me} in {race.Car}");
    }

    /// <summary>
    /// Pre-hook on the first screen's "should I keep going?" method. Always
    /// lets it run: it is what ticks the car loader, and it is also what winds
    /// the screen down on its last pass. Only the answer changes, and that is
    /// ScreenAnswered's job.
    /// </summary>
    public static void ScreenAsks(CpuContext c, IMemory m)
    {
        if (!_armed) return;

        bool ready = CarLoad.TheRoomsCarIsLoaded(m);
        if (!ready && DateTime.UtcNow - _armedAt < CarPatience)
        {
            Waiting(m);
            return;
        }

        _armed = false;
        _endNow = true;
        EndedTheScreen = true;

        m.WriteU8(ExitByte, TheRace);

        // What a player's last page pop would have done. The race answer does
        // not do it - it goes straight to the exit byte - because by then the
        // music is already stopped, and a launch that walks no pages has to
        // stop it itself or leave the sequencer reading into the next overlay.
        Call(c, m, StopTheArcadeMusic);

        Console.Error.WriteLine(ready
            ? "[direct] the car is loaded - letting the arcade screen finish"
            : $"[direct] the car did not load in {CarPatience.TotalSeconds:F0}s - going anyway"
              + $" (the loader stopped on step {CarLoad.StepIn(m, 0)})");

        // The arcade is done; what draws from here is the game's own pre-race
        // screen, which is the loading step and is meant to be seen.
        ArcadeCurtain.Drop(ready ? "the car is loaded" : "the car never loaded");
    }

    /// <summary>
    /// Post-hook on the same method. The screen has just done everything a
    /// normal last pass does; all that is left is to answer zero, which is what
    /// the loop reads to know it is over - the same answer the menu's own race
    /// case arrives at.
    ///
    /// Skipping the body instead, which an earlier version did, ends the screen
    /// without its last pass ever running - and the race then followed a
    /// display list nobody had wound down.
    /// </summary>
    public static void ScreenAnswered(CpuContext c, IMemory m)
    {
        if (!_endNow) return;
        _endNow = false;
        c.V0 = 0u;
    }

    /// <summary>
    /// Post-hook on the pre-race screen's own "should I keep going?" method,
    /// answering zero the first time it is asked.
    ///
    /// That screen is what kills a launched race. It renders, and a pointer it
    /// renders through is wrong on this path - the writes land on a VBlank
    /// callback node at 0x801C949C, and the handler then calls whatever a
    /// primitive left where the callback was. Finding the pointer means reading
    /// the GTE-heavy drawing code, which is a long way past anything else here.
    ///
    /// It is also skippable. It lives half a second in a walked run, and the
    /// 720-byte block it appears to sit between is byte for byte identical
    /// either side of it - so it decides nothing a race needs. Ending it at
    /// once is the same trick that worked on the first screen, and it never
    /// draws a frame to go wrong in.
    /// </summary>
    /// <summary>
    /// Whether to skip the pre-race screen. Off unless GT2_SKIP_PRERACE says
    /// otherwise, because it is the game's own way out of the arcade.
    ///
    /// It was skipped twice, both times for rendering through a pointer that
    /// landed on the sound callback node at 0x801C949C - and both times the
    /// race block it draws from was wrong, because the parameter builder was
    /// falling through its branch on a clobbered A2. A screen drawing a race
    /// out of a block nobody filled is exactly what a wrong pointer looks like.
    /// With the builder fixed the reason to skip it is gone, and its own code
    /// says what it is for: slot 0x24 counts down a frame budget and sets the
    /// state at +0x19A to 3 every pass, which is a fade. Skipping the fade is
    /// what left the sound sequencer running into an overlay that had already
    /// replaced its stream.
    /// </summary>
    static readonly bool SkipPreRace =
        Environment.GetEnvironmentVariable("GT2_SKIP_PRERACE") is not (null or "");

    public static void PreRaceScreenAnswered(CpuContext c, IMemory m)
    {
        if (!SkipPreRace || !EndedTheScreen || _preRaceEnded) return;
        _preRaceEnded = true;
        c.V0 = 0u;
        Console.Error.WriteLine("[direct] the pre-race screen is ended before it draws");
    }

    static bool _preRaceEnded;

    /// <summary>The pair that puts the sound sequencer on and off the VBlank list.</summary>
    const uint UnregisterSoundCallback = 0x80068708u;

    /// <summary>
    /// Takes the sound sequencer off the VBlank list before the race loads.
    ///
    /// The sequencer walks a byte stream, and once the race overlay has landed
    /// on top of the arcade's the stream is gone - it then reads a sequence
    /// byte from an address that is not memory, out of an interrupt, which is
    /// where a launched race died with everything else already right.
    ///
    /// Blunt, and worth saying so: the game has a way to stop its music that
    /// this has not found, and the pre-race screen is probably where it lives.
    /// Removing the callback stops the reading rather than stopping the music,
    /// so a launched race may be quiet until that is found.
    /// </summary>
    /// <summary>
    /// Off unless GT2_SILENCE_ARCADE asks for it.
    ///
    /// Nothing in the game calls 0x80068708. The matching register runs once at
    /// boot, from 0x80010E54, and the callback it installs is the sound
    /// driver's whole tick - so taking it off is not "stopping the arcade's
    /// music", it is stopping every sound the process will ever make, for good.
    /// That is why a launched race was silent.
    ///
    /// It was worth having while the pre-race screen was skipped, since with
    /// nothing fading the music out the sequencer read on into an overlay that
    /// had replaced its stream. The screen runs again now, so the game does its
    /// own fade and this stays off.
    /// </summary>
    static readonly bool SilenceArcade =
        Environment.GetEnvironmentVariable("GT2_SILENCE_ARCADE") is not (null or "");

    static void SilenceTheArcade(CpuContext c, IMemory m)
    {
        if (!SilenceArcade) return;
        Call(c, m, UnregisterSoundCallback);
        Console.Error.WriteLine("[direct] the sound sequencer is taken off the VBlank list");
    }

    /// <summary>
    /// Points the captured block at the room's course.
    ///
    /// The capture describes the race it was taken from, Tahiti Road included,
    /// and the course is the last thing in it that still does. The room carries
    /// an asset code; the block wants the number the game's own roster pairs
    /// with that course's name, so the roster is what turns one into the other.
    /// </summary>
    static void PutTheRoomsCourseIn(IMemory m)
    {
        CourseRoster.Say(m, "as the block is built");

        string code = CourseWanted.Length > 0 ? CourseWanted : _race?.Course ?? "";
        if (code.Length == 0)
        {
            Console.Error.WriteLine("[course] the room names no course - the capture's will run");
            return;
        }

        // The code is enough: the game's number for a course is a rotate-and-add
        // hash of exactly this string. No roster lookup, and no matching by the
        // name a player reads, which could never have worked - the disc has
        // three courses called Tahiti Road.
        uint id = CourseId.Of(code);
        m.WriteU32(Parameters + ChosenCourse, id);

        // And the name beside it, which is the capture's until it is written.
        string name = CourseTable.DisplayName(code);
        for (int i = 0; i < NameRoom; i++)
            m.WriteU8(Parameters + CourseName + (uint)i, (byte)(i < name.Length ? name[i] : 0));

        Console.Error.WriteLine($"[course] the race is set to {name} ({code}, 0x{id:X8})");
    }

    /// <summary>
    /// Points the captured block at the paint this machine's player chose.
    ///
    /// An index, and only this player's: the builder resolves it against the
    /// car it was given and fills entrant zero from the result. The other five
    /// are RaceGrid's, and by the letter rather than the index.
    /// </summary>
    static void PutTheRoomsPaintIn(IMemory m)
    {
        if (_race is not { } race) return;

        var mine = race.Players.FirstOrDefault(p => p.Name == race.Me);
        if (mine is null) return;

        var paints = race.Cars?.Colours(mine.Car);
        if (paints is null || mine.Colour >= paints.Count)
        {
            Console.Error.WriteLine(
                $"[paint] no paint {mine.Colour} for {mine.Car} - the capture's will run");
            return;
        }

        m.WriteU16(Parameters + ChosenPaint, mine.Colour);
        Console.Error.WriteLine(
            $"[paint] {race.Me} is driving {mine.Car} in paint {mine.Colour}"
            + $" of {paints.Count} ('{(char)paints[mine.Colour].Letter}')");
    }

    /// <summary>
    /// Runs one of the game's own functions from inside a hook and puts every
    /// register back, returning what the call left in V0.
    ///
    /// Restoring is the whole point, and leaving it out is what made a launched
    /// race come out with no tyres. A pre-hook runs on the same context the
    /// function it precedes is about to read its arguments from, and the
    /// parameter builder's first instruction is FP = A2. Calling anything at
    /// all from that hook without restoring leaves A2 holding whatever the call
    /// used it for, so the builder took the block's mode byte from a garbage
    /// address, matched none of its three modes, and fell straight through the
    /// branch that resolves the player's car and fills the grid.
    ///
    /// The arguments go in here rather than at the call site so that setting
    /// them cannot leak either: the snapshot is taken before they are written.
    /// </summary>
    static uint Call(CpuContext c, IMemory m, uint address, uint a0 = 0u, uint a1 = 0u)
    {
        var saved = c.Snapshot();
        try
        {
            c.A0 = a0;
            c.A1 = a1;
            RecompOne.Runtime.Dispatch.Dispatcher.Call(c, m, address);
            return c.V0;
        }
        finally
        {
            c.Restore(saved);
        }
    }

    static byte[]? _parameters;

    /// <summary>
    /// Where the parameter block names the car that was chosen.
    ///
    /// The builder's mode-4 path reads the id at +0x10 three times over - once
    /// for the colour byte at +0x16, once for the table select at +0x14, and
    /// once to resolve the record - and never looks at +0x0C. The other modes
    /// do, so both are written and they are kept equal, which is how a walked
    /// race leaves them.
    /// </summary>
    const uint ChosenCar = 0x0Cu;
    const uint ChosenCarAgain = 0x10u;

    /// <summary>
    /// Where the block names the paint - as an index into the car's own list,
    /// which is the one place the game wants the index rather than the letter.
    ///
    /// The builder hands it straight to
    /// gt2_ovr3_read_signed_byte_from_decoded_car_info_at_offset along with the
    /// car id, and that returns the letter it then puts in the entrant. So this
    /// is where the player's own car is painted, and RaceGrid's write is where
    /// everyone else's is.
    /// </summary>
    const uint ChosenPaint = 0x16u;

    /// <summary>
    /// Where the parameter block names the course.
    ///
    /// Not as text. The builder hands this u32 to 0x8005E590, which stores it
    /// at [race+0x40] and looks it up in the roster the game keeps at
    /// 0x801E18E0 to get the name it copies into [race+0x20] - which is why a
    /// captured block reads "Tahiti Road" at +0xB8 and yet carries no course
    /// code anywhere in it. See CourseRoster.
    /// </summary>
    const uint ChosenCourse = 0x1B8u;

    /// <summary>Where it keeps the course's name as text, and how much room there is.</summary>
    const uint CourseName = 0xB8u;
    const int NameRoom = 0x20;

    /// <summary>
    /// A course to run instead of the room's, when GT2_COURSE names one the way
    /// the game names it. The room is the real source; this is how the roster
    /// gets tried against a track without hosting a lobby to pick it.
    /// </summary>
    static readonly string CourseWanted = Environment.GetEnvironmentVariable("GT2_COURSE") ?? "";

    /// <summary>
    /// Pre-hook on the arcade parameter builder.
    ///
    /// Built without the menus the block comes out all zeroes, since it is
    /// assembled from what the screens decided - the course reads out of it as
    /// text at +0xB8. So a captured one is supplied.
    ///
    /// Before rather than after, which the first version got wrong. The builder
    /// reads the chosen car out of this block at +0x10, looks the car's record
    /// up and fills the entrant from it; writing the block afterwards left the
    /// entrant filled from whatever was there and the race running one car's
    /// model with another's figures.
    ///
    /// So the captured block is written first and the room's car patched into
    /// it, and the game fills the entrant itself - which is the whole of the
    /// fifty bytes RaceGrid was never going to write correctly by hand.
    ///
    /// The course is still the capture's, whatever the room says.
    /// </summary>
    public static void ParametersBuilt(CpuContext c, IMemory m)
    {
        if (!EndedTheScreen) return;

        _parameters ??= File.Exists(ParametersPath) ? File.ReadAllBytes(ParametersPath) : null;
        if (_parameters is not { Length: >= ParametersSize })
        {
            Console.Error.WriteLine($"[direct] no race parameters at {ParametersPath}");
            return;
        }

        for (int i = 0; i < ParametersSize; i++)
            m.WriteU8(Parameters + (uint)i, _parameters[i]);

        // The capture names the car it was taken with. Naming the room's
        // instead is the whole fix: the builder resolves it to the car's own
        // record and fills the entrant from that, so nothing here has to know
        // what those fields mean.
        string car = _race?.Car ?? "";
        if (CarInfo.TryEncodeCode(car, out uint packed))
        {
            m.WriteU32(Parameters + ChosenCar, packed);
            m.WriteU32(Parameters + ChosenCarAgain, packed);
            Console.Error.WriteLine(
                $"[direct] the race parameters are supplied from a capture, driving {car}");
        }
        else
        {
            Console.Error.WriteLine(
                $"[direct] the race parameters are supplied from a capture,"
                + $" but {car} is not a car id - the capture's car will drive");
        }

        PutTheRoomsCourseIn(m);
        PutTheRoomsPaintIn(m);

        Say(m, "as supplied");
        SilenceTheArcade(c, m);
    }


    /// <summary>
    /// Post-hook on the builder: names the room's entrants in the race the
    /// builder has just finished.
    ///
    /// The car is not this method's business. The builder reads the chosen id
    /// out of the block, resolves it through 0x80010000, builds the whole race
    /// through func_80010554 and puts the player's car into the block at +0x1C
    /// through load_car_parts - all of it from the id patched in beforehand. So
    /// what is left here is the part the arcade has no idea about: who else is
    /// on the grid.
    /// </summary>
    /// <summary>
    /// The head of the parameter block, which is what the builder branches on.
    ///
    /// +0x02 chooses among its modes and +0x06 decides, when negative, whether
    /// the player's car record is resolved and handed to the fill. The capture
    /// holds 4 and -1. Whether the block still holds them when the builder
    /// looks is the question a launched race keeps failing on, and printing it
    /// either side of the builder is the cheapest way to stop guessing.
    /// </summary>
    static void Say(IMemory m, string when)
    {
        Console.Error.WriteLine(
            $"[direct] the parameter block {when}:"
            + $" +0x00={m.ReadU8(Parameters):X2} +0x01={m.ReadU8(Parameters + 1u):X2}"
            + $" +0x02={m.ReadU8(Parameters + 2u):X2} +0x06={(short)m.ReadU16(Parameters + 6u)}"
            + $" +0x0C=0x{m.ReadU32(Parameters + 0x0Cu):X8}"
            + $" +0x10=0x{m.ReadU32(Parameters + 0x10u):X8}");
    }

    public static void RaceBuilt(CpuContext c, IMemory m)
    {
        Say(m, "as the builder left it");

        // Before the room, and before the early return, for the same reason as
        // the replay below: how many laps a race is run over is a property of
        // the race rather than of the room, so an arcade race reached without a
        // lobby has to be able to answer the question too.
        RaceLaps.PutTheLapsIn(m);

        // Before the room, and before the early return: GT2_REPLAY_VIEW exists
        // to try a replay on one machine with no lobby at all, and a switch
        // that only works when a room has been agreed cannot do that. This is
        // where the first attempt at it went wrong - the log said nothing
        // because nothing ran.
        bool replay = ReplayView.ShowsAReplay(_race) && ReplayView.MakeItAReplay(m);

        if (_race is not { } race)
        {
            if (replay) ReplayView.KeepItAReplay(m);
            return;
        }

        // Only the room's entrants, and only their identities.
        //
        // RaceLauncher used to install a whole captured race block over this
        // one first. That made sense when the builder was falling through its
        // branch and leaving nothing behind, and it is exactly wrong now: the
        // builder fills all six entrants from the parameter block, and the
        // template - captured on Tahiti Road - put that course's number back at
        // +0x40 and its name back at +0x20 over the top. Every launched race
        // ran Tahiti Road because a Tahiti Road race was being written over the
        // one the game had just built correctly.
        if (!RaceGrid.TryApply(m, race.Players, race.Me, race.Cars))
            Console.Error.WriteLine("[direct] the race is not built yet - the room was not applied");

        // After the grid, because the grid writes +0x82 the way an arcade race
        // means it and a replay means it differently.
        if (replay) ReplayView.KeepItAReplay(m);

        SecondDriver.HandTheSecondCarOver(m);
        SayTheRacesCourse(m);

        // The VBlank callback list is sound here and nonsense a moment later,
        // so this is where a watch on it wants to start looking.
        RecompOne.Runtime.Memory.MemoryWatch.Arm();
    }

    /// <summary>Where the race block keeps the course it was set to.</summary>
    const uint RaceBlock = 0x801D585Cu;
    const uint CourseNameInRace = 0x20u;
    const uint CourseIdInRace = 0x40u;

    /// <summary>
    /// What the game decided the race's course is, in its own words.
    ///
    /// 0x8005E590 writes both of these: the number raw at +0x40 and the name it
    /// looked that number up to get at +0x20. So this is the game answering the
    /// question rather than the port assuming it, and it is what separates
    /// "the write did not reach the race" from "it reached it and the race
    /// loads its course from somewhere else entirely".
    /// </summary>
    static void SayTheRacesCourse(IMemory m)
    {
        var name = new System.Text.StringBuilder();
        for (uint i = 0; i < 0x20; i++)
        {
            byte b = m.ReadU8(RaceBlock + CourseNameInRace + i);
            if (b == 0) break;
            name.Append((char)b);
        }

        Console.Error.WriteLine(
            $"[course] the race block says 0x{m.ReadU32(RaceBlock + CourseIdInRace):X8} \"{name}\"");
    }

    /// <summary>
    /// Says how the wait is going, when it changes and once a second besides. A
    /// car that does not arrive is a loader step that stops advancing, and the
    /// step separates a load that never starts from one that stalls.
    /// </summary>
    static void Waiting(IMemory m)
    {
        byte step = CarLoad.StepIn(m, 0);
        var now = DateTime.UtcNow;

        // Every frame, not only when the log says something: the curtain is
        // what the player is looking at, and a number that only moves once a
        // second looks like a number that has stopped.
        ArcadeCurtain.Doing = $"fetching {_race?.Car ?? "the car"} - step {step} of 8";

        if (_said && step == _step && now - _lastSaid < TimeSpan.FromSeconds(1)) return;

        _said = true;
        _step = step;
        _lastSaid = now;
        Console.Error.WriteLine(
            $"[direct] holding the arcade screen: the loader is on step {step}"
            + $" after {(now - _armedAt).TotalSeconds:F1}s");
    }
}
