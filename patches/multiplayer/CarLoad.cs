using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Asks the game to load a car.
///
/// A race started outside the menus dies for want of the player's car object,
/// and building one from outside is not the answer: gt2_03 already has a
/// function that does the whole job. `func_800162C0(request, owner, id)` looks
/// the packed five-character id up in the car table, cancels whatever the
/// request was doing, writes the file index and both file sizes into it, and
/// sets it going. The arcade's frame loop then ticks the request through eight
/// steps until the car is in memory.
///
/// So the port loads a car by making one call with the code the room already
/// carries. What it cannot know statically is the owner: it lives on the main
/// stack, a local of the arcade's own flow rather than an object at a fixed
/// address. But the owner is handed to the loader on every tick, so watching
/// one tick is enough to learn it.
///
/// The layout this rests on is recorded in
/// docs/superpowers/specs/2026-08-23-race-start-findings.md.
/// </summary>
public static class CarLoad
{
    /// <summary>gt2_03's enqueue: A0 the request, A1 the owner, A2 the packed id.</summary>
    const uint Enqueue = 0x800162C0u;

    /// <summary>Where the owner keeps the pointers to its request records.</summary>
    const int FirstSlot = 0x228;
    const int SlotStride = 4;

    /// <summary>How many cars the arcade prepares at once.</summary>
    public const int Slots = 2;

    /// <summary>Which of the loader's eight steps a request is due to run.</summary>
    const int Step = 0x10;

    /// <summary>The step number past the last one, which a finished request holds.</summary>
    const int Finished = 9;

    static uint _owner;

    /// <summary>
    /// A car to ask for, as a five-character code, when GT2_CAR_ASK names one.
    ///
    /// This is how the mechanism gets proved before anything is built on it:
    /// run the arcade with GT2_CAR_ASK and GT2_LOAD_TRACE together, and the
    /// load trace either shows the game fetching that car's files or it does
    /// not. Nothing in the game asks for this, so it stays off by default.
    /// </summary>
    static readonly string Wanted = Environment.GetEnvironmentVariable("GT2_CAR_ASK") ?? "";

    static bool _asked;
    static bool _reported;

    /// <summary>
    /// The car this machine's player is to drive, as a five-character code,
    /// and the file index that code resolves to.
    ///
    /// The two are not the same question. The code is what to ask for; the
    /// index is what the request record holds once the ask has gone through,
    /// so comparing against it is how this tells "already loading the right
    /// car" from "still loading the arcade's". Resolving it needs the archive
    /// rather than the game, which is why the caller supplies it.
    /// </summary>
    static string _driving = "";
    static int _drivingIndex = -1;

    /// <summary>Where a request records the file it is fetching.</summary>
    const int FileIndex = 0x04;

    /// <summary>
    /// How many times a car may be asked for before this gives up.
    ///
    /// The arcade asks for its own car whenever the player changes it, and
    /// this asks back - two pieces of code writing one record. They settle
    /// once the player stops choosing, but a cap means a disagreement that
    /// does not settle reports itself instead of asking for ever.
    /// </summary>
    const int Insistence = 8;

    static int _insisted;

    /// <summary>
    /// Pre-hook on func_80016640, whose second argument is the owner.
    ///
    /// Cheaper than it looks: the loader is ticked once per request per frame,
    /// so this is a couple of stores a frame, and it is the only route to an
    /// address that is a stack local rather than a symbol.
    /// </summary>
    public static void Ticking(CpuContext c, IMemory m)
    {
        _owner = c.A1;
        Insist(c, m);

        if (Wanted.Length == 0) return;

        // Only on the tick that belongs to the slot being asked about, since
        // the loader ticks each request in turn and asking during another
        // slot's turn would reset a record that is not the one meant.
        if (c.A0 != RequestIn(m, 0)) return;

        if (!_asked)
        {
            _asked = TryAsk(c, m, 0, Wanted);
            return;
        }

        if (_reported || !DoneIn(m, 0)) return;
        _reported = true;
        Console.Error.WriteLine($"[car] {Wanted} finished loading");
    }

    /// <summary>
    /// Says which car this machine's player is to drive, so that whatever the
    /// arcade loaded gets replaced by the one the room agreed on.
    ///
    /// The lobby and the arcade menus ask two different questions and the
    /// player answers both, so the car on the track is whichever was asked for
    /// last. Left alone that is the arcade's, which is not the one the other
    /// players were told about.
    /// </summary>
    public static void Drive(string code, int fileIndex)
    {
        _driving = code;
        _drivingIndex = fileIndex;
        _insisted = 0;
    }

    /// <summary>Stops replacing the arcade's car, once the race has it.</summary>
    public static void StopDriving()
    {
        _driving = "";
        _drivingIndex = -1;
    }

    /// <summary>
    /// Asks again for the car the room agreed on, whenever the record holds a
    /// different one. Runs on the loader's own tick, which is the only moment
    /// the record is certain to be between steps.
    /// </summary>
    static void Insist(CpuContext c, IMemory m)
    {
        // Only on the tick belonging to the slot being replaced: the record is
        // certain to be between steps then, and asking during another slot's
        // turn would reset a record that is not the one meant.
        if (c.A0 != RequestIn(m, 0)) return;

        if (GaveUp(m))
        {
            _insisted++;
            Console.Error.WriteLine(
                $"[car] gave up asking for {_driving}; the arcade keeps loading"
                + $" file {m.ReadU16(RequestIn(m, 0) + FileIndex)} instead of {_drivingIndex}");
            return;
        }

        if (!WantsTheRoomsCar(m)) return;

        _insisted++;
        TryAsk(c, m, 0, _driving);
    }

    /// <summary>
    /// Whether slot 0 is fetching something other than the car the room agreed
    /// on, and there is still patience left to say so.
    /// </summary>
    internal static bool WantsTheRoomsCar(IMemory m)
    {
        if (_driving.Length == 0 || _drivingIndex < 0) return false;

        uint request = RequestIn(m, 0);
        if (request == 0u) return false;
        if (m.ReadU16(request + FileIndex) == _drivingIndex) return false;

        return _insisted < Insistence;
    }

    /// <summary>The one tick on which giving up is worth saying out loud.</summary>
    static bool GaveUp(IMemory m)
    {
        if (_driving.Length == 0 || _drivingIndex < 0 || _insisted != Insistence) return false;

        uint request = RequestIn(m, 0);
        return request != 0u && m.ReadU16(request + FileIndex) != _drivingIndex;
    }

    /// <summary>Whether a tick has been seen, which is what knowing the owner takes.</summary>
    public static bool Ready => _owner != 0u;

    /// <summary>The owner the last tick named, or zero before any tick.</summary>
    public static uint Owner => _owner;

    /// <summary>The request record in <paramref name="slot"/>, or zero if there is none.</summary>
    public static uint RequestIn(IMemory m, int slot)
    {
        if (_owner == 0u || slot < 0 || slot >= Slots) return 0u;
        return m.ReadU32(_owner + (uint)(FirstSlot + slot * SlotStride));
    }

    /// <summary>
    /// Asks for the car named by <paramref name="code"/> in <paramref name="slot"/>.
    /// False when the code is not a car id, or before a tick has named the owner.
    ///
    /// The call runs on the game's own context because that is what it reads
    /// its arguments from, so the registers are put back afterwards: a caller
    /// is in the middle of its own work and did not ask to have them changed.
    /// </summary>
    public static bool TryAsk(CpuContext c, IMemory m, int slot, string code)
    {
        if (!Ready) return false;
        if (!CarInfo.TryEncodeCode(code, out uint packed)) return false;

        uint request = RequestIn(m, slot);
        if (request == 0u) return false;

        var saved = c.Snapshot();
        try
        {
            c.A0 = request;
            c.A1 = _owner;
            c.A2 = packed;
            Dispatcher.Call(c, m, Enqueue);
        }
        finally
        {
            c.Restore(saved);
        }

        Console.Error.WriteLine(
            $"[car] asked for {code} in slot {slot}"
            + $" (request 0x{request:X8}, owner 0x{_owner:X8})");
        return true;
    }

    /// <summary>
    /// Whether the request in <paramref name="slot"/> has run out of steps,
    /// which is how a caller knows the car is in memory and the race can go.
    /// A slot that was never asked for anything reads as finished, since it is
    /// not something to wait on.
    /// </summary>
    public static bool DoneIn(IMemory m, int slot)
    {
        uint request = RequestIn(m, slot);
        if (request == 0u) return true;

        byte step = m.ReadU8(request + Step);
        return step == 0 || step >= Finished;
    }

    /// <summary>Forgets everything, for a test that must not inherit another's.</summary>
    internal static void Forget()
    {
        _owner = 0u;
        _asked = false;
        _reported = false;
        _driving = "";
        _drivingIndex = -1;
        _insisted = 0;
    }
}
