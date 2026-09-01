using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// What a race ended as, for the machine that ran it: how many laps its driver
/// completed and how long the race took.
///
/// Both come from the game rather than being timed here, which is the point.
/// A port that ran its own stopwatch would be timing the wall clock while the
/// game timed the race, and the two disagree by every frame the game spent
/// loading, paused, or running slow.
///
/// **Laps** is the short at <see cref="Laps"/> of a car. It was found by asking
/// which fields move a handful of times in a whole race rather than every
/// frame, and confirmed across two races that differed: 3 after three laps, 2
/// after two, never falling.
///
/// **The time** took longer, and three searches failed before a fourth found
/// it. Counting rises and falls over halfwords finds no clock at all, because a
/// 32-bit counter's low half wraps every 65536 and that reads as a fall.
/// Counting words found two - 0x800A8D72, which counts frames exactly, and
/// 0x800A8C64, which counts two per frame - and neither is the race time: both
/// were already running before the lights went green. Which is also why the
/// search could never have worked, since a clock reset at the green light falls
/// once. And it is nowhere in the race's own 64 kilobytes, in any unit tried.
///
/// So the whole of RAM was kept at the moment the race overlay was replaced,
/// and searched for the number the screen had shown. A race that ended at
/// 2:21.456 held **141456** - milliseconds, exactly - in three places, and the
/// three are not equally believable:
///
///   0x801D5F80   0x198 past the end of the race record at 0x801D585C, among
///                entries of five words on a 0x14 stride reading
///                -1 -1 -1 -1 -65536, which is what an unset time looks like
///   0x8005AC80   inside a repeating 0x20-byte structure of large constants
///   0x801B75D4   surrounded by values in the hundreds of millions
///
/// The first is the one that looks like a race result and the other two look
/// like data that happens to contain the number. That is a good argument and
/// not a measurement, so all three are read and reported until a race says
/// which of them tracks the screen. Naming the right one then costs nothing.
/// </summary>
public static class RaceResult
{
    /// <summary>
    /// Which lap this car is on, counted from one - **not** how many it has
    /// completed.
    ///
    /// It was read as completed laps for a while, and two readings of the same
    /// race caught it. A one-minute race called its last lap while the car was
    /// still on lap one and the HUD then showed "Lap 1/2", because the count
    /// written was this plus one. And the standings said two laps where the
    /// game's own results screen listed one. Both are the same off-by-one, and
    /// both go away by reading this as the lap in progress.
    ///
    /// So laps completed is this less one, and never below zero.
    /// </summary>
    public const uint OnLap = 0x634u;

    /// <summary>
    /// The race time, in milliseconds.
    ///
    /// Chosen from the three addresses that held it rather than proven to be
    /// it - see the account above. GT2_RACE_TIME_AT moves it, so if a race
    /// shows one of the others tracking the screen instead, saying so costs a
    /// run rather than a build.
    /// </summary>
    public static readonly uint Milliseconds =
        uint.TryParse(Environment.GetEnvironmentVariable("GT2_RACE_TIME_AT"),
                      System.Globalization.NumberStyles.HexNumber, null, out uint at)
            ? at : 0x801D5F80u;

    /// <summary>
    /// The other two, kept and printed beside it. They cost one line a race and
    /// they are what would name the right one the moment this one is seen to
    /// disagree with the screen.
    /// </summary>
    static readonly uint[] AlsoHeldIt = [0x8005AC80u, 0x801B75D4u];

    /// <summary>
    /// The counter that rises by exactly two every frame, which is the rate the
    /// screen's own clock runs at.
    ///
    /// It is the only clock of the game's that can be read while a race is
    /// running. <see cref="Milliseconds"/> is written when a race ends and
    /// reads zero throughout - a lap turning mid-race reported the race clock
    /// as "--:--.---", which settles a question the notes had left open.
    ///
    /// Free-running since long before the lights, so only differences mean
    /// anything: the gap between two laps turning is a lap time, in sixtieths.
    /// </summary>
    public const uint Sixtieths = 0x800A8C64u;

    /// <summary>How many of those go by in a second.</summary>
    public const int PerSecond = 60;

    /// <summary>The race clock as it reads now, in sixtieths.</summary>
    public static int TicksNow(IMemory m) => (int)m.ReadU32(Sixtieths);

    static int InMilliseconds(int ticks) => (int)(ticks * 1000L / PerSecond);

    /// <summary>What this machine's driver did, as the game recorded it.</summary>
    public readonly record struct Finish(int Laps, int Milliseconds, int BestLapMilliseconds = 0)
    {
        /// <summary>The time as the game would show it: m:ss.mmm.</summary>
        public string Clock => Show(Milliseconds);

        /// <summary>And the best lap of it, the same way.</summary>
        public string BestLap => Show(BestLapMilliseconds);

        /// <summary>Whether a lap was ever finished, and so timed.</summary>
        public bool HasABestLap => BestLapMilliseconds > 0;

        static string Show(int ms) =>
            ms <= 0 ? "--:--.---"
            : $"{ms / 60000}:{ms % 60000 / 1000:00}.{ms % 1000:000}";
    }

    /// <summary>
    /// The highest lap this machine's own car has been seen to be on.
    ///
    /// Watched during the race rather than read at the end of it, because at
    /// the end it reads zero: a race that had plainly been driven reported
    /// "0 laps" beside a race time that was exactly right. The end-of-race
    /// snapshot settles why - every one of the six cars reads 0 there, so it is
    /// the teardown that clears the counter, not this port reading the wrong
    /// car. And the overlay being replaced is the only moment this port hears
    /// that a race is over, which is already too late.
    ///

    /// The highest seen rather than the last seen, for the same reason: the
    /// last frame before the end may already be the one that cleared it.
    /// </summary>
    static int _mostLaps;

    /// <summary>
    /// Says when the counter turns, so a race can be held against its own
    /// results screen.
    ///
    /// A one-minute timed race ended with this reporting two laps while the
    /// game's results screen listed one, of 2:41.391 - and the clock had run
    /// out at sixty seconds with the first lap still unfinished, its lap time
    /// and total time both reading 1:03.604. Two readings of the same race
    /// differing by one is either this counting the crossing that starts the
    /// race or the game not counting the one that ends it, and the difference
    /// matters: in a timed race the winner is whoever completed the most laps.
    ///
    /// So each turn is timestamped against the game's own race clock. A first
    /// turn at 1:03.604 says the counter follows the line; a first turn at zero
    /// says it counts the start.
    /// </summary>
    /// <summary>
    /// When the last lap turned, and the shortest gap between two turns - which
    /// is this driver's best lap.
    ///
    /// Measured between the laps rather than read out of the game, because the
    /// only clock that runs while a race does is the one that counts sixtieths.
    /// So a lap time here is exact to a sixtieth of a second and the screen's
    /// own is finer - which is honest to say and good enough to qualify on,
    /// since every machine measures the same way from the same counter.
    /// </summary>
    static int _lastTurn = -1;
    static int _bestLapTicks;

    public static void Watch(IMemory m)
    {
        int lap = OnLapNow(m, 0);
        if (lap <= _mostLaps || lap >= 1000) return;

        _mostLaps = lap;

        int ticks = TicksNow(m);
        if (_lastTurn >= 0)
        {
            int thisLap = ticks - _lastTurn;
            if (thisLap > 0 && (_bestLapTicks == 0 || thisLap < _bestLapTicks))
                _bestLapTicks = thisLap;
        }
        _lastTurn = ticks;

        Console.Error.WriteLine(
            $"[result] the car is now on lap {lap} - {Completed(lap)} completed"
            + $" - best lap so far {new Finish(0, 0, InMilliseconds(_bestLapTicks)).BestLap}");
    }

    /// <summary>Forgets the race just run, so the next one counts its own laps.</summary>
    public static void Forget()
    {
        _mostLaps = 0;
        _lastTurn = -1;
        _bestLapTicks = 0;
    }

    /// <summary>Which lap a car is on, as the game counts it.</summary>
    public static int OnLapNow(IMemory m, int slot) => (short)m.ReadU16(
        RemoteCars.FirstCarObject + (uint)(slot * RemoteCars.CarStride) + OnLap);

    /// <summary>How many laps a car on this lap has finished.</summary>
    public static int Completed(int onLap) => Math.Max(0, onLap - 1);

    /// <summary>
    /// Reads what the race just ended as, for the car in the given slot.
    ///
    /// Called at the moment the race overlay is replaced, which is the last
    /// instant the race's own memory is still standing - and so the last moment
    /// the time can be read at all. The laps come from what was watched rather
    /// than from what is there now, which by this point is zero.
    /// </summary>
    public static Finish Read(IMemory m, int slot)
    {
        int onLap = Math.Max(OnLapNow(m, slot), _mostLaps);
        return new Finish(
            Completed(onLap), (int)m.ReadU32(Milliseconds), InMilliseconds(_bestLapTicks));
    }

    /// <summary>
    /// Says what every candidate held, so the next race names the real one.
    ///
    /// Printed beside the laps, because the two together are checkable against
    /// a screen anybody can read: a two-lap race that took 2:21.456 should say
    /// 2 laps and 2:21.456, and whichever address does not is not the time.
    /// </summary>
    public static void Say(IMemory m, int slot)
    {
        var finish = Read(m, slot);
        Console.Error.WriteLine(
            $"[result] car {slot} finished {finish.Laps} lap(s) in {finish.Clock}"
            + $", best lap {finish.BestLap}"
            + $" - the car is on lap {OnLapNow(m, slot)}, the highest seen was {_mostLaps}"
            + " - the two that also held the time read "
            + string.Join("  ", AlsoHeldIt.Select(a => $"0x{a:X8}={AsATime(m, a)}")));
    }

    static string AsATime(IMemory m, uint at)
    {
        int ms = (int)m.ReadU32(at);
        return ms <= 0 || ms > 60 * 60 * 1000
            ? ms.ToString()
            : $"{ms} ({new Finish(0, ms).Clock})";
    }
}
