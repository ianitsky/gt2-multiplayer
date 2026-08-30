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
    const int AiSkill = 0x42;          // 0 for the human's car, 100 for the rest
    const int IsAi = 0x82;             // 0 for the human's car, 1 for the rest
    const int GridPlace = 0x8D;        // counted from zero
    const int CarName = 0x90;
    const int CarNameRoom = 0x18;      // how much space the name has

    /// <summary>
    /// The entrants in the order this machine writes them.
    ///
    /// The human always drives entrant 0 - proven by putting the local player
    /// anywhere else and watching them drive entrant 0's car regardless - so
    /// each machine leads with its own player and the rest keep the room's
    /// order behind them. Every machine therefore holds the same six cars in a
    /// different rotation, which is why anything sent between them has to be
    /// keyed by a seat in the room rather than by a slot in the race.
    /// </summary>
    public static List<Player> Order(IReadOnlyList<Player> players, string me, int racing)
    {
        var order = players.Take(racing).ToList();
        int mine = order.FindIndex(p => p.Name == me);
        if (mine > 0)
        {
            var self = order[mine];
            order.RemoveAt(mine);
            order.Insert(0, self);
        }
        return order;
    }

    /// <summary>Which slot this machine drives <paramref name="who"/> in, or -1.</summary>
    public static int SlotFor(IReadOnlyList<Player> players, string me, string who) =>
        Order(players, me, Math.Min(players.Count, Slots)).FindIndex(p => p.Name == who);

    /// <summary>The most entrants the block has room for.</summary>
    public const int Slots = 6;

    /// <summary>
    /// Writes <paramref name="players"/> into the race the menu has just built,
    /// marking <paramref name="me"/> as the one this machine drives. Returns
    /// false if the block is not a finished race yet, so a caller can keep
    /// trying until it is.
    /// </summary>
    public static bool TryApply(IMemory m, IReadOnlyList<Player> players, string me, CarCatalogue? cars)
    {
        if (players.Count == 0) return false;

        // The menu fills the entrants one at a time while the host is still
        // choosing. Writing into a half-built race would just be overwritten
        // when the choice is confirmed.
        for (int i = 0; i < Slots; i++)
            if (m.ReadU32(Block + (uint)(FirstEntrant + i * EntrantSize) + CarId) == 0) return false;

        int racing = Math.Min(players.Count, Slots);
        m.WriteU8(Block + Count, (byte)racing);

        // The human always drives entrant 0 - proven by putting the local
        // player anywhere else and watching them drive entrant 0's car
        // regardless of which entrant was marked. So each machine leads with
        // its own player, and the entrant order differs from machine to
        // machine by exactly that rotation.
        var order = Order(players, me, racing);

        for (int i = 0; i < racing; i++)
        {
            var player = order[i];
            uint entrant = Block + (uint)(FirstEntrant + i * EntrantSize);

            if (CarInfo.TryEncodeCode(player.Car, out uint packed))
                m.WriteU32(entrant + CarId, packed);

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
            int place = players.Take(racing).ToList().FindIndex(p => p.Name == player.Name);
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
