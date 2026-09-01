using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Finds the fields a race is run by, by watching which of them move and how.
///
/// Three questions have been asked with this, and the first two are answered.
///
/// **Where a car begins.** 0x800A9688, stepping by 0xB40 - printed rather than
/// deduced, by hooking the function that draws a car and reporting the pointer
/// it is handed. <see cref="RemoteCars.FirstCar"/> is 0x47C into a car rather
/// than at its start, which cost two wrong answers the moment gt2_01's own
/// offsets were read against it.
///
/// **What a wheel is drawn at.** One short per wheel at +0x7C6 + wheel * 0x10,
/// found by counting how often each halfword of a car moved over 1200 frames of
/// driving. Two guesses taken from reading gt2_01 - a speed at +0x5A, three
/// angles at +0x7CC - were both wrong: the first reads 500 through acceleration
/// and braking alike, and the second holds the four wheels' mounting positions.
///
/// **How far a car has got, and how long it has taken.** Neither is a field
/// this port knows, and both have to come from the game rather than be timed
/// here. A lap counter is counted rather than integrated, so it moves a handful
/// of times in a whole race and cannot show up in a report tuned for what moves
/// constantly. A race clock only ever rises. So this counts three things per
/// field - how often it moved, how often it rose, how often it fell - and
/// reports on whichever question is being asked:
///
///   GT2_CAR_LOOK=1       what moves constantly
///   GT2_CAR_LOOK=rare    what moves a handful of times - a lap, a gear, a place
///   GT2_CAR_LOOK=clock   what only ever rises - a time, a distance, a counter
///
/// Two places are watched, because a race is not all in one: the car the game
/// draws first, and the head of the race context at 0x800A9500, which is where
/// something belonging to the race rather than to a car would live.
/// </summary>
public static class CarDriving
{
    static readonly string Wanted =
        Environment.GetEnvironmentVariable("GT2_CAR_LOOK") ?? "";

    static bool Looking => Wanted.Length > 0;
    static bool Rare => string.Equals(Wanted, "rare", StringComparison.OrdinalIgnoreCase);
    static bool Clock => string.Equals(Wanted, "clock", StringComparison.OrdinalIgnoreCase);

    /// <summary>How many frames between reports.</summary>
    static readonly int Every =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_CAR_LOOK_EVERY"), out int n) && n > 0
            ? n : 600;

    /// <summary>
    /// The most times a halfword may move and still count as rare. A three-lap
    /// race turns a lap counter three times; twenty leaves room for a gearbox.
    /// </summary>
    static readonly int AtMost =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_CAR_LOOK_ATMOST"), out int n) && n > 0
            ? n : 20;

    /// <summary>
    /// How often a halfword has to move to be worth printing when the question
    /// is what moves constantly, as a share of the frames watched.
    /// </summary>
    const double Often = 0.02;

    /// <summary>
    /// The second place to watch, which starts below the race context rather
    /// than at it.
    ///
    /// The context's own head - 0x800A9500 to the car array at +0x188 - held no
    /// clock, and neither did any car. But the code that reads a car's lap
    /// counter finishes by writing a word at 0x800A8D6C, which is 0x794 *below*
    /// the context: there is race state down there, and a race time is exactly
    /// the kind of thing that would live in it.
    ///
    /// GT2_CAR_LOOK_AT and GT2_CAR_LOOK_LEN move the window, so sweeping for
    /// something costs a run rather than a rebuild.
    /// </summary>
    static readonly uint Watching =
        uint.TryParse(Environment.GetEnvironmentVariable("GT2_CAR_LOOK_AT"),
                      System.Globalization.NumberStyles.HexNumber, null, out uint at)
            ? at : 0x800A8000u;

    static readonly int HowMuch =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_CAR_LOOK_LEN"),
                     System.Globalization.NumberStyles.HexNumber, null, out int len) && len > 0
            ? len : 0x1688;

    /// <summary>Every pointer the drawing code has been handed a car through.</summary>
    static readonly SortedSet<uint> _drawnFrom = [];

    static Watched? _car;
    static Watched? _context;

    /// <summary>
    /// Pre-hook on gt2_ovr1_race_car_build_the_matrices_it_is_drawn_from, which
    /// takes the car in A0 - so this runs once per car per frame, with the car
    /// the game itself chose rather than one this port worked out.
    /// </summary>
    public static void MatricesBeingBuilt(CpuContext c, IMemory m)
    {
        if (!Looking) return;

        uint car = c.A0;
        if (_drawnFrom.Add(car))
        {
            long fromArray = (long)car - RemoteCars.FirstCarObject;
            Console.Error.WriteLine(
                $"[drive] a car is drawn from 0x{car:X8}"
                + $" - {fromArray:+#;-#;0} from the car array"
                + (fromArray >= 0 && fromArray % RemoteCars.CarStride == 0
                    ? $", which is car {fromArray / RemoteCars.CarStride} of it"
                    : ", which is not a car of it"));

            _car ??= new Watched("the first car drawn", car, RemoteCars.CarStride);
            _context ??= new Watched(
                $"the race state around 0x{Watching:X8}", Watching, HowMuch);
        }

        if (_car is not { } watched || car != watched.At) return;

        watched.Sample(m);
        _context!.Sample(m);

        if (watched.Frames % Every != 0) return;
        Console.Error.WriteLine(watched.Report(m));
        Console.Error.WriteLine(_context.Report(m));
    }

    /// <summary>Forgets the race just run, so the next one counts its own.</summary>
    public static void Forget()
    {
        _car = null;
        _context = null;
        _drawnFrom.Clear();
    }

    /// <summary>
    /// A stretch of memory, and what each halfword of it has been seen to do.
    ///
    /// Three counts rather than one: how often it changed, how often it rose,
    /// how often it fell. A field that only ever rises is a clock or a counter
    /// however fast it moves, and telling that apart from something that
    /// oscillates is the whole of what separates a race time from a suspension
    /// travel.
    /// </summary>
    sealed class Watched(string name, uint at, int length)
    {
        public uint At => at;
        public int Frames { get; private set; }

        readonly short[] _was = new short[length / 2];
        readonly int[] _moved = new int[length / 2];

        /// <summary>
        /// And the same again over words, which is what a clock has to be
        /// counted in.
        ///
        /// A race time is thirty-two bits, and a thirty-two bit counter's low
        /// half wraps every 65536 - which reads as a fall, so counting rises
        /// and falls over halfwords finds no clock however plainly one is
        /// running. It found none, twice, which is what said to count words.
        /// </summary>
        readonly int[] _wasWord = new int[length / 4];
        readonly int[] _rose = new int[length / 4];
        readonly int[] _fell = new int[length / 4];

        public void Sample(IMemory m)
        {
            bool first = Frames == 0;
            for (int i = 0; i < _was.Length; i++)
            {
                short now = (short)m.ReadU16(at + (uint)(i * 2));
                if (!first && now != _was[i]) _moved[i]++;
                _was[i] = now;
            }

            for (int i = 0; i < _wasWord.Length; i++)
            {
                int now = (int)m.ReadU32(at + (uint)(i * 4));
                if (!first && now != _wasWord[i])
                {
                    if (now > _wasWord[i]) _rose[i]++; else _fell[i]++;
                }
                _wasWord[i] = now;
            }
            Frames++;
        }

        /// <summary>
        /// Whether the field at <paramref name="i"/> answers the question asked
        /// - counted in halfwords for the two questions about movement, and in
        /// words for the one about a clock.
        /// </summary>
        bool Interesting(int i) =>
            Rare ? _moved[i] > 0 && _moved[i] <= AtMost
            : Clock ? _rose[i] > 0 && _fell[i] == 0
            : _moved[i] > (int)(Frames * Often);

        /// <summary>How wide the thing being counted is.</summary>
        int Step => Clock ? 4 : 2;

        int Fields => Clock ? _wasWord.Length : _was.Length;

        public string Report(IMemory m)
        {
            var said = new System.Text.StringBuilder();
            int from = -1, count = 0;

            for (int i = 0; i <= Fields; i++)
            {
                bool live = i < Fields && Interesting(i);
                if (live && from < 0) { from = i; count = 0; }
                if (live) count++;
                if (live || from < 0) continue;

                int moved = 0;
                for (int j = from; j < from + count; j++)
                    moved = Math.Max(moved, Clock ? _rose[j] : _moved[j]);

                said.Append($"{Environment.NewLine}[drive]   +0x{from * Step:X3}..+0x{(from + count) * Step - 1:X3}"
                    + $"  moved {moved}"
                    + (Clock ? $" of {Frames}, never down" : "")
                    + "  now " + string.Join(" ", Enumerable.Range(from, count)
                        .Select(j => Clock
                            ? ((int)m.ReadU32(at + (uint)(j * 4))).ToString()
                            : ((short)m.ReadU16(at + (uint)(j * 2))).ToString())));
                from = -1;
            }

            return $"[drive] {name} at 0x{at:X8}, after {Frames} frames, what moved "
                + (Rare ? $"between 1 and {AtMost} times:"
                   : Clock ? "and never fell:"
                   : "constantly:")
                + (said.Length == 0 ? " nothing" : said.ToString());
        }
    }
}
