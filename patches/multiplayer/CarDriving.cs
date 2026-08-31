using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Reports the fields a car is *driven* by, as opposed to the ones it is placed
/// by, so the visible half of a remote car can be built from measurement.
///
/// Place and heading already travel, and a car that has them is in the right
/// spot facing the right way with its wheels dead still. What is missing is
/// everything the game derives rather than stores: the wheels turning, the
/// wheels steering, the brake lights, the body pitching under load. None of
/// that is sent, and none of it is guessed either - the game works it out from
/// the car's own state, and a remote car's state is whatever is left after this
/// port teleports it every frame without telling it it is moving.
///
/// So the question is not "which visual effects are there" but "which few
/// numbers does the game derive them from". Two came out of reading gt2_01:
///
///   +0x5A   the car's speed along its own nose.
///           gt2_ovr1_race_car_build_the_matrices_it_is_drawn_from reads it,
///           makes the vector (0, 0, speed), turns it by the car's matrix and
///           adds the result shifted down eight into the drawn position. That
///           is a car moving between physics steps, so this is the number that
///           says a car is moving at all.
///
///   +0x7CC + wheel * 0x10
///           the three angles each wheel is drawn at, four wheels, set by
///           gt2_ovr1_race_car_set_its_four_wheels_draw_angles. The first is
///           the steer, negated on one side of the car; the third comes out of
///           the wheel records the pointer at +0x878 leads to, which is the
///           spin.
///
/// Both offsets are from whatever base that code is handed, and this port's car
/// array is the one thing it can address - so whether they are the same base is
/// exactly what this reports. A field that tracks the throttle is the right
/// field; one that sits still while the car accelerates is not.
///
/// Off unless GT2_CAR_LOOK is set.
/// </summary>
public static class CarDriving
{
    static readonly bool Looking =
        Environment.GetEnvironmentVariable("GT2_CAR_LOOK") is not (null or "");

    /// <summary>How often to report, in frames - often enough to follow a throttle.</summary>
    static readonly int Every =
        int.TryParse(Environment.GetEnvironmentVariable("GT2_CAR_LOOK_EVERY"), out int n) && n > 0
            ? n : 30;

    /// <summary>The car's speed along its own nose, as the drawn position uses it.</summary>
    const uint Speed = 0x5Au;

    /// <summary>The first wheel's three draw angles, and the step to the next wheel.</summary>
    const uint FirstWheel = 0x7CCu;
    const uint WheelStride = 0x10u;
    const int Wheels = 4;

    /// <summary>
    /// The two shorts the steer is divided out of, kept because they are the
    /// nearest thing to a steering angle found so far and either one moving
    /// with the wheel is what would say so.
    /// </summary>
    const uint SteerOver = 0x41Cu;
    const uint SteerUnder = 0x410u;

    static int _frame;

    public static void Tick(IMemory m, int cars)
    {
        if (!Looking) return;
        if (_frame++ % Every != 0) return;

        var said = new System.Text.StringBuilder();
        for (int car = 0; car < cars && car < RemoteCars.Cars; car++)
        {
            uint at = RemoteCars.FirstCar + (uint)(car * RemoteCars.CarStride);

            said.Append($"{Environment.NewLine}[drive]   car {car}"
                + $"  speed {(short)m.ReadU16(at + Speed),6}"
                + $"  steer {(short)m.ReadU16(at + SteerOver),6}/{(short)m.ReadU16(at + SteerUnder),6}"
                + "  wheels");

            for (uint wheel = 0; wheel < Wheels; wheel++)
            {
                uint w = at + FirstWheel + wheel * WheelStride;
                said.Append($"  ({(short)m.ReadU16(w),6},{(short)m.ReadU16(w + 4u),6})");
            }
        }

        Console.Error.WriteLine($"[drive] frame {_frame}:" + said);
    }

    /// <summary>Forgets the race just run, so the next one counts its own frames.</summary>
    public static void Forget() => _frame = 0;
}
