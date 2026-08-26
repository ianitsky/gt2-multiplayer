using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Starts a race without the player walking the arcade's menus.
///
/// The arcade overlay's entry point is the whole arcade: it initialises a
/// screen object, runs a screen over it, reads one byte to choose among five
/// exits, and - for exit 1 - builds a block of race parameters, runs a second
/// screen, copies the block out and loads the race. Everything a race needs is
/// in that sequence, and every step of it has been read.
///
/// What cannot be done is faking a screen's result. A screen does not return a
/// value the loop inspects; it runs as a task and longjmps, and the loop falls
/// through only when the screen is genuinely finished. So getting past a screen
/// means never entering it, which means replacing the entry point rather than
/// steering it.
///
/// That is what this does: when a lobby has settled a race, the arcade's body
/// is skipped and the same sequence runs here with the two screens left out.
/// Three findings make it safe to leave them out:
///
///   - the loader's owner is built by the initialisation, four milliseconds
///     into the entry point and long before any screen;
///   - the 720-byte parameter block is byte for byte identical before and
///     after the screens, so they contribute nothing to it;
///   - what the player chooses in a screen lands in the race block at
///     0x801D585C, which the room supplies instead.
///
/// One thing the screens did have to be replaced: they are what ticks the car
/// loader. With them gone this ticks it, which is why the car is loaded here
/// rather than left to the frame loop.
///
/// The layout and the evidence are in
/// docs/superpowers/specs/2026-08-23-race-start-findings.md.
/// </summary>
public static class DirectRace
{
    // ---- the arcade's own addresses, in the order the race case uses them ----

    /// <summary>How much stack the arcade's entry point claims.</summary>
    const uint Frame = 0x4D8;

    /// <summary>Where in that frame the screen object lives.</summary>
    const uint ScreenObject = 0x10;

    /// <summary>Scratch the race case uses for the two values it seeds from.</summary>
    const uint Scratch = 0x4C8;

    const uint Initialise = 0x80011954u;
    const uint PrepareScreenObject = 0x80013B7Cu;
    const uint ConstructFirstScreen = 0x80013BD8u;

    /// <summary>What the arcade hands the constructor as its second argument.</summary>
    const uint FirstScreenSetup = 0x800521C0u;

    /// <summary>
    /// The first screen's per-frame handler, which is more than its name in the
    /// arcade suggests: on its first pass it installs the two car request
    /// records, and on every pass after it ticks them.
    ///
    /// Nothing calls it by address - the arcade reaches it through a pointer,
    /// four milliseconds after the entry point - so without calling it here the
    /// request slots hold whatever was in memory. A first attempt read
    /// 0x8005DB7C out of one, which is code, and asked the game to load a car
    /// through it.
    ///
    /// Its argument is the screen object, which the ordering probe reported
    /// directly rather than leaving to be worked out.
    ///
    /// Calling it per frame is also how the load is driven. Reaching past it to
    /// the loader itself is not enough: the loader's third step polls a
    /// decompression that only this advances, so a launch that ticked the
    /// loader alone stalled there for ever.
    /// </summary>
    const uint ScreenFrame = 0x80013BE4u;

    const uint Seed = 0x8007D23Cu;
    const uint SeedStep = 0x80083AE0u;
    const uint BuildParameters = 0x80010C84u;
    const uint Parameters = 0x801C3350u;
    const int ParametersSize = 0x2D0;

    /// <summary>Where the arcade copies the parameters on its way into the race.</summary>
    const uint ParametersGoTo = 0x801D5FA0u;

    const uint ConstructPreRaceScreen = 0x80014898u;
    const uint LeavePreRaceScreen = 0x800148CCu;

    /// <summary>Two flags the arcade raises just before the copy.</summary>
    const uint Flags = 0x801EF5F0u;

    const uint LoadOverlayDefault = 0x8005DA3Cu;
    const uint LoadOverlay = 0x8005DA7Cu;
    const uint RaceOverlayEntry = 0x80011F64u;

    /// <summary>How long to keep ticking the loader before giving up on the car.</summary>
    static readonly TimeSpan CarPatience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Off unless GT2_DIRECT_LAUNCH is set. Skipping the arcade replaces a
    /// path that works with one that has never run, so it stays behind a
    /// switch until it has.
    /// </summary>
    public static bool Enabled { get; } =
        Environment.GetEnvironmentVariable("GT2_DIRECT_LAUNCH") is not (null or "");

    /// <summary>What the lobby settled, or null when nothing is waiting to start.</summary>
    public sealed record Pending(IReadOnlyList<Player> Players, string Me, string Car, CarCatalogue? Cars);

    static Pending? _waiting;

    /// <summary>Called by the lobby when a race has been agreed and is to start.</summary>
    public static void Expect(Pending race)
    {
        if (!Enabled)
        {
            Console.Error.WriteLine("[direct] a race is ready but GT2_DIRECT_LAUNCH is not set - using the menus");
            return;
        }
        _waiting = race;
        Console.Error.WriteLine($"[direct] a race is waiting: {race.Players.Count} players, {race.Me} in {race.Car}");
    }

    /// <summary>
    /// Pre-hook on the arcade's entry point. Returns true to let the arcade
    /// run as it always has, false to skip it because the race has been run
    /// here instead.
    /// </summary>
    public static bool InsteadOfTheArcade(CpuContext c, IMemory m)
    {
        if (_waiting is not { } race) return true;

        // Taken before anything can fail: a launch that throws halfway must not
        // be retried on the next visit to the arcade with half its work done.
        _waiting = null;

        // No falling back. Running the arcade's own body after this has failed
        // partway looked like the kind thing to do and is not: the body starts
        // by initialising everything this has already initialised, and doing
        // that twice hangs the game with the window unable to answer. A failure
        // that stops here says what went wrong; one that falls back says it and
        // then hangs, which reads as a different fault entirely.
        Run(c, m, race);
        return false;
    }

    static void Run(CpuContext c, IMemory m, Pending race)
    {
        Console.Error.WriteLine("[direct] starting a race without the menus");

        // The arcade's prologue, because everything below is written in terms
        // of its frame - the screen object at +0x10 above all.
        uint caller = c.SP;
        c.SP = caller - Frame;
        uint frame = c.SP;

        try
        {
            uint screen = frame + ScreenObject;

            // Initialisation. This is what builds the screen object and, inside
            // it, installs the two car request records - which is why skipping
            // the screens does not cost the car loader its owner.
            Call(c, m, Initialise);
            c.A0 = screen;
            Call(c, m, PrepareScreenObject);
            c.A0 = screen;
            c.A1 = FirstScreenSetup;
            Call(c, m, ConstructFirstScreen);

            c.A0 = screen;
            Call(c, m, ScreenFrame);

            // Told before the slots are read rather than when the car is asked
            // for: RequestIn answers zero when it has no owner, so checking
            // first would report an empty slot whatever the install did.
            CarLoad.UseOwner(screen);
            RefuseAnImpossibleRequest(m);

            // Here the arcade would run its first screen and then switch on the
            // exit byte. Exit 1 is the race, and what follows is exit 1.

            c.A0 = 0u;
            Call(c, m, Seed);
            m.WriteU32(frame + Scratch, c.V0);

            c.A0 = frame + Scratch;
            Call(c, m, SeedStep);
            uint first = c.V0;

            c.A0 = frame + Scratch;
            Call(c, m, SeedStep);

            c.A0 = first;
            c.A1 = c.V0;
            c.A2 = Parameters;
            Call(c, m, BuildParameters);

            // The room's race, written where the arcade's screens would have
            // written the player's.
            if (!RaceLauncher.TryPrepare(m, race.Players, race.Me, race.Cars))
                throw new InvalidOperationException("the race block could not be prepared");

            LoadTheCar(c, m, screen, race.Car);

            c.A0 = screen;
            Call(c, m, ConstructPreRaceScreen);
            c.A0 = screen;
            c.A1 = 2u;
            Call(c, m, LeavePreRaceScreen);

            m.WriteU8(Flags + 1u, 1);
            m.WriteU8(Flags + 2u, 1);

            // The arcade copies its parameters out in sixteen-byte steps and
            // then one word more, which is 0x2D4 bytes rather than the 0x2D0
            // the loop bound suggests.
            for (uint i = 0; i < ParametersSize + 4; i += 4)
                m.WriteU32(ParametersGoTo + i, m.ReadU32(Parameters + i));

            c.A0 = 3u;
            Call(c, m, LoadOverlayDefault);

            c.A0 = 0u;
            c.A1 = RaceOverlayEntry;
            c.A2 = 0u;
            Call(c, m, LoadOverlay);

            Console.Error.WriteLine("[direct] the race overlay is loaded");
        }
        finally
        {
            c.SP = caller;
        }
    }

    /// <summary>
    /// Stops if the request slots do not hold what a request record looks like.
    ///
    /// Asking the game to load a car through a bad pointer does not fail where
    /// it is asked: the enqueue writes a file index and two lengths through it
    /// and the damage surfaces later, in the drive, as an address that is not
    /// memory. Better to say which pointer was wrong while that is still the
    /// question being asked.
    /// </summary>
    static void RefuseAnImpossibleRequest(IMemory m)
    {
        uint request = CarLoad.RequestIn(m, 0);

        // The records live in data, well below the code the overlays load at
        // and well below the stack. Anything else is not one.
        if (request is >= 0x80020000u and < 0x801F0000u) return;

        throw new InvalidOperationException(
            $"the car request slot holds 0x{request:X8}, which is not a request record");
    }

    /// <summary>
    /// Loads the player's car, ticking the loader here because the screens that
    /// would normally tick it are not running.
    /// </summary>
    static void LoadTheCar(CpuContext c, IMemory m, uint owner, string car)
    {
        CarLoad.UseOwner(owner);
        // Carrying on without the car is what the race cannot survive: the
        // overlay loads, dereferences an object nobody built, and dies several
        // layers away as a call to something that is not code. Stopping here
        // costs the race and keeps the reason.
        if (!CarLoad.TryAsk(c, m, 0, car))
            throw new InvalidOperationException($"the game would not be asked to load {car}");

        var until = DateTime.UtcNow + CarPatience;

        // Where it got to, not just that it did not get there. The loader is
        // eight steps and a load that stalls is one step that stops advancing,
        // so the step it stalled on is the whole diagnosis - and the steps it
        // did reach say whether it stalled at once or partway.
        byte step = CarLoad.StepIn(m, 0);
        var reached = new List<byte> { step };

        while (!CarLoad.DoneIn(m, 0))
        {
            if (DateTime.UtcNow > until)
                throw new InvalidOperationException(
                    $"{car} stalled on step {CarLoad.StepIn(m, 0)} of the loader"
                    + $" after reaching {string.Join(", ", reached)}");

            // The screen's own frame, not the loader underneath it: the steps
            // depend on work this drives and the loader alone cannot finish.
            c.A0 = owner;
            Call(c, m, ScreenFrame);

            byte now = CarLoad.StepIn(m, 0);
            if (now != step) { step = now; reached.Add(now); }

            // The steps wait on the drive, and nothing else is advancing it.
            RecompOne.Runtime.Runtime.PumpHost();
        }

        Console.Error.WriteLine($"[direct] {car} is loaded, through steps {string.Join(", ", reached)}");
    }

    /// <summary>
    /// Calls one of the game's functions.
    ///
    /// The return address is left alone. Nothing here reads it: a function that
    /// returns normally comes back through the dispatcher, and one that longjmps
    /// takes its destination from the jmp_buf and overwrites RA on the way. What
    /// would matter is clearing it, since the port's boot loop treats RA as
    /// where to carry on when a resume point returns.
    /// </summary>
    static void Call(CpuContext c, IMemory m, uint address) => Dispatcher.Call(c, m, address);
}
