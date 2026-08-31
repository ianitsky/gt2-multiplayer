using RecompOne.Runtime.Memory;

namespace GT2Port.Multiplayer;

/// <summary>
/// Puts the room's players on the grid.
///
/// A race is one block in RAM, and the game builds it from the arcade menu as
/// the host chooses a car and a track. Everything a multiplayer race needs is
/// already in that block - it just describes the wrong six drivers. So let the
/// menu build it, then write the room over the top: one entrant per player,
/// each with their own car, and the grid places dealt out in room order.
///
/// Every machine writes the same entrants in the same places, so all of them
/// agree on who starts where. What differs is one byte: each marks its own
/// player as the car a human drives, and the others as ones the game drives.
/// That is what stops six machines from all steering the same car.
///
/// The block's layout is recorded in
/// docs/superpowers/specs/2026-08-23-race-start-findings.md.
/// </summary>
public static class RaceGrid
{
    const uint Block = 0x801D585Cu;

    const int Count = 0x5A;            // how many entrants the race has
    const int FirstEntrant = 0x5C;
    const int EntrantSize = 0xD0;
    const int CarId = 0x00;

    /// <summary>
    /// The paint, as the letter the car's own list names it by - not as an
    /// index into that list.
    ///
    /// gt2_ovr3_build_race_block_and_fill_all_six_entrants reads the letter
    /// out of the twelve-byte record it is handed per car and writes it here,
    /// sign-extended to a word. Writing the index instead would be writing the
    /// question where the game keeps the answer, which is the same mistake the
    /// rotation cost seven attempts.
    /// </summary>
    const int PaintLetter = 0x04;
    const int AiSkill = 0x42;          // 0 for the human's car, 100 for the rest
    const int IsAi = 0x82;             // 0 for the human's car, 1 for the rest
    const int GridPlace = 0x8D;        // counted from zero
    const int CarName = 0x90;
    const int CarNameRoom = 0x18;      // how much space the name has

    /// <summary>
    /// The entrants in the order this machine writes them.
    ///
    /// Entrant 0 is the one that matters: the human always drives it - proven
    /// by putting the local player anywhere else and watching them drive
    /// entrant 0's car regardless - and the view follows it. So each machine
    /// leads with the driver it is built around and the rest keep the room's
    /// order behind them. Every machine therefore holds the same six cars in a
    /// different rotation, which is why anything sent between them has to be
    /// keyed by a seat in the room rather than by a slot in the race.
    ///
    /// <paramref name="leader"/> is this machine's own player when it is
    /// racing, and the driver it is watching when it is not. A viewer's race
    /// is otherwise the race that driver's own machine builds - which is what
    /// makes a viewer cost no new mechanism at all.
    /// </summary>
    public static List<Player> Order(IReadOnlyList<Player> drivers, string leader, int racing)
    {
        var order = drivers.Take(racing).ToList();
        int first = order.FindIndex(p => p.Name == leader);
        if (first > 0)
        {
            var lead = order[first];
            order.RemoveAt(first);
            order.Insert(0, lead);
        }
        return order;
    }

    /// <summary>Which slot this machine holds <paramref name="who"/> in, or -1.</summary>
    public static int SlotFor(IReadOnlyList<Player> drivers, string leader, string who) =>
        Order(drivers, leader, Math.Min(drivers.Count, Slots)).FindIndex(p => p.Name == who);

    /// <summary>The most entrants the block has room for.</summary>
    public const int Slots = 6;

    /// <summary>
    /// The letter a player's chosen paint is called, or null when the car
    /// database cannot say - in which case the entrant keeps whatever the
    /// arcade left there, which is a paint the car really has.
    /// </summary>
    static sbyte? PaintFor(CarCatalogue? cars, Player player)
    {
        var paints = cars?.Colours(player.Car);
        if (paints is null || player.Colour >= paints.Count) return null;
        return paints[player.Colour].Letter;
    }

    /// <summary>
    /// Writes <paramref name="drivers"/> into the race the menu has just built,
    /// leading with <paramref name="leader"/> - this machine's own player when
    /// it is racing, the driver it is watching when it is not. Returns false if
    /// the block is not a finished race yet, so a caller can keep trying until
    /// it is.
    /// </summary>
    public static bool TryApply(IMemory m, IReadOnlyList<Player> drivers, string leader, CarCatalogue? cars)
    {
        if (drivers.Count == 0) return false;

        // The menu fills the entrants one at a time while the host is still
        // choosing. Writing into a half-built race would just be overwritten
        // when the choice is confirmed.
        for (int i = 0; i < Slots; i++)
            if (m.ReadU32(Block + (uint)(FirstEntrant + i * EntrantSize) + CarId) == 0) return false;

        int racing = Math.Min(drivers.Count, Slots);
        m.WriteU8(Block + Count, (byte)racing);

        // The human always drives entrant 0 - proven by putting the local
        // player anywhere else and watching them drive entrant 0's car
        // regardless of which entrant was marked. So each machine leads with
        // its own player, and the entrant order differs from machine to
        // machine by exactly that rotation.
        var order = Order(drivers, leader, racing);

        for (int i = 0; i < racing; i++)
        {
            var player = order[i];
            uint entrant = Block + (uint)(FirstEntrant + i * EntrantSize);

            if (CarInfo.TryEncodeCode(player.Car, out uint packed))
                m.WriteU32(entrant + CarId, packed);

            // Every machine writes every entrant's paint, its own included, so
            // six machines paint the same six cars - which is the whole of
            // what choosing a colour in the lobby is for.
            if (PaintFor(cars, player) is { } letter)
                m.WriteU32(entrant + PaintLetter, unchecked((uint)letter));

            // The name is a separate field and is not derived from the id, so
            // leaving it alone would have the HUD announce a car that is not
            // on the track.
            WriteText(m, entrant + CarName, cars?.DisplayName(player.Car) ?? player.Car, CarNameRoom);

            bool human = i == 0;
            m.WriteU8(entrant + IsAi, (byte)(human ? 0 : 1));
            m.WriteU8(entrant + AiSkill, (byte)(human ? 0 : 100));

            // The place comes from the room, not from the entrant slot. Slots
            // are rotated so each machine drives its own car, but the room's
            // order is the same everywhere - so every machine puts every
            // player in the same place on the grid, however it numbers them.
            int place = drivers.Take(racing).ToList().FindIndex(p => p.Name == player.Name);
            m.WriteU8(entrant + GridPlace, (byte)(place < 0 ? i : place));
        }

        return true;
    }

    static void WriteText(IMemory m, uint at, string text, int room)
    {
        for (int i = 0; i < room; i++)
            m.WriteU8(at + (uint)i, i < text.Length && text[i] < 0x80 ? (byte)text[i] : (byte)0);
    }
}
