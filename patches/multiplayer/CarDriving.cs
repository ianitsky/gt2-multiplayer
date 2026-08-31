using RecompOne.Runtime.Context;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Finds the fields a car is *driven* by, as opposed to the ones it is placed
/// by, by watching which of them move.
///
/// Place and heading already travel, and a car that has them is in the right
/// spot facing the right way with its wheels dead still. What is missing is
/// everything the game derives rather than stores - wheels turning, wheels
/// steering, brake lights, the body pitching under load - and a remote car has
/// none of the state those come from, because this port teleports it every
/// frame without telling it it is moving.
///
/// Two guesses at which fields those are came out of reading gt2_01, and both
/// were wrong in a way worth keeping:
///
///   +0x5A    read by gt2_ovr1_race_car_build_the_matrices_it_is_drawn_from
///            into the vector (0, 0, it), turned by the car's matrix and added
///            to the drawn position - which looked exactly like speed. It reads
///            500 and stays there while the car accelerates, brakes and stops,
///            so it is a fixed offset along the nose and not a speed at all.
///
///   +0x7CC + wheel * 0x10
///            written by gt2_ovr1_race_car_set_its_four_wheels_draw_angles,
///            which does write three angles per wheel at exactly that stride.
///            The memory holds (-3055, -5004), (3055, -5004), (-3063, 5024),
///            (3063, 5024) and never changes - left and right, front and rear.
///            Those are where the wheels are mounted, so either that function
///            does not run in this race or it is handed something other than a
///            car.
///
/// What is not in doubt is where a car begins: this hooks the function that
/// draws one and prints the pointer it is handed, and the six came back as
/// 0x800A9688 stepping by 0xB40 - the race context's own car array.
///
/// So the base is known and the offsets are not, which is what this now
/// measures. It keeps a copy of the followed car and counts, per halfword, how
/// often it changes. A field that moves every frame while the car is driven is
/// live state; one that never moves is setup. Naming what to look at is then a
/// matter of reading the ranges that come out, rather than guessing at them.
///
/// Off unless GT2_CAR_LOOK is set.
/// </summary>
public static class CarDriving
{
    static readonly bool Looking =
        Environment.GetEnvironmentVariable("GT2_CAR_LOOK") is not (null or "");

    /// <summary>How many frames between reports.</summary>
    static readonly int Every =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_CAR_LOOK_EVERY"), out int n) && n > 0
            ? n : 600;

    /// <summary>
    /// How often a halfword has to move to be worth printing, as a share of the
    /// frames watched. Low enough to catch a gear change, high enough that a
    /// field written once at the start does not fill the report.
    /// </summary>
    static readonly double Often = 0.02;

    /// <summary>Every pointer the drawing code has been handed a car through.</summary>
    static readonly SortedSet<uint> _drawnFrom = [];

    /// <summary>The first of them, which is the one this follows.</summary>
    static uint _following;

    static byte[]? _was;
    static int[]? _moved;
    static int _frames;

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
            if (_following == 0u) _following = car;
        }

        if (car != _following) return;

        Watch(m, car);
    }

    /// <summary>
    /// Counts how often each halfword of the car moves, and reports the ones
    /// that move at all.
    /// </summary>
    static void Watch(IMemory m, uint car)
    {
        int halves = RemoteCars.CarStride / 2;
        _was ??= new byte[RemoteCars.CarStride];
        _moved ??= new int[halves];

        bool first = _frames == 0;
        for (int i = 0; i < halves; i++)
        {
            uint at = car + (uint)(i * 2);
            byte low = m.ReadU8(at), high = m.ReadU8(at + 1u);
            if (!first && (low != _was[i * 2] || high != _was[i * 2 + 1])) _moved[i]++;
            _was[i * 2] = low;
            _was[i * 2 + 1] = high;
        }

        if (++_frames % Every != 0) return;

        int enough = (int)(_frames * Often);
        var said = new System.Text.StringBuilder();
        int from = -1, count = 0;

        for (int i = 0; i <= halves; i++)
        {
            bool live = i < halves && _moved[i] > enough;
            if (live && from < 0) { from = i; count = 0; }
            if (live) count++;
            if (live || from < 0) continue;

            int busiest = 0;
            for (int j = from; j < from + count; j++) busiest = Math.Max(busiest, _moved[j]);
            said.Append($"{Environment.NewLine}[drive]   +0x{from * 2:X3}..+0x{(from + count) * 2 - 1:X3}"
                + $"  {count * 2,4} bytes  moved up to {busiest}/{_frames}"
                + $"  now {(short)m.ReadU16(car + (uint)(from * 2)),7}");
            from = -1;
        }

        Console.Error.WriteLine(
            $"[drive] 0x{car:X8} after {_frames} frames, what moved more than {enough} times:" + said);
    }

    /// <summary>Forgets the race just run, so the next one counts its own.</summary>
    public static void Forget()
    {
        _frames = 0;
        _following = 0u;
        _was = null;
        _moved = null;
        _drawnFrom.Clear();
    }
}
