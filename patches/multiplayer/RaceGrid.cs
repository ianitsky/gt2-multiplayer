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
/// The block's layout was worked out by reading the game and is written
/// up in this repository's history.
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
    /// <summary>
    /// Where on the grid a car stands, counted from zero - and it is this,
    /// rather than the entrant's index, that the game stands a car by.
    ///
    /// Measured on four machines at once. Each holds the same four drivers in
    /// its own rotation, and each reports the same square for the same number:
    /// place 0 at (713339, 358892), place 1 at (695895, 314351), place 2 at
    /// (654697, 336622), place 3 at (636879, 291710). Slot 0 is a different
    /// square on every machine, so the slot is not what decides.
    /// </summary>
    const int GridPlace = 0x8D;
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

    /// <summary>
    /// Which slot this machine holds <paramref name="who"/> in, or -1.
    ///
    /// Counted from <see cref="FirstRoomEntrant"/>, which is the room's first
    /// entrant and not always the race's: when the game is driving, the room
    /// starts at entrant two and the two before it are the seats for people.
    /// </summary>
    public static int SlotFor(IReadOnlyList<Player> drivers, string leader, string who)
    {
        int inTheRoom = Order(drivers, leader, RoomSize(drivers)).FindIndex(p => p.Name == who);
        return inTheRoom < 0 ? -1 : inTheRoom + FirstRoomEntrant;
    }

    /// <summary>How many of the room's drivers a race has room for.</summary>
    static int RoomSize(IReadOnlyList<Player> drivers) =>
        Math.Min(drivers.Count, Slots - FirstRoomEntrant);

    /// <summary>The most entrants the block has room for.</summary>
    public const int Slots = 6;

    /// <summary>
    /// Whether to number the entrants nobody in the room is driving.
    ///
    /// Tried on, and it put every car on one square. The question it was asking
    /// is now answered - <see cref="GridPlace"/> is what stands a car - so what
    /// is left is why numbering the spare entrants 4 and 5 broke the four that
    /// were racing. A place past the entrant count reaching past the end of the
    /// course's list of squares would do it. Left off, and left as a switch,
    /// because the room's own four are numbered 0 to 3 and are correct.
    /// </summary>
    static readonly bool PlaceTheLeftovers =
        Environment.GetEnvironmentVariable("GT2_GRID_LEFTOVERS") is not (null or "");

    /// <summary>
    /// Whether to let the game drive this machine's own car.
    ///
    /// For testing, and it earns its keep the moment a test needs more than one
    /// machine: four instances of a race cannot be driven by one person, so
    /// everything about a four-player race - the grid, the colours, the wheels,
    /// the results coming back, the room surviving - could only ever be checked
    /// by someone driving one car and watching three drift into a wall.
    ///
    /// An entrant is marked as the game's to drive by +0x82 and given a skill
    /// at +0x42, which is what the other five already get. This gives entrant
    /// zero - this machine's own - the same marks.
    ///
    /// GT2_AI_DRIVES on its own means a skill of 100; a number sets it, so a
    /// room of machines can be told to drive at different speeds and produce a
    /// finishing order worth reading.
    ///
    /// What this cannot say is whether the pad is also still driving that car.
    /// The port already knows the pad follows entrant zero whatever the entrant
    /// is marked as, so both may steer at once - which for an unattended test
    /// is harmless, since nobody is holding the pad.
    /// </summary>
    static readonly string AiAsked =
        Environment.GetEnvironmentVariable("GT2_AI_DRIVES") ?? "";

    public static bool AiDrives => AiAsked.Length > 0;

    /// <summary>
    /// The first entrant the room's drivers are written into.
    ///
    /// Zero normally. Two when the game is being asked to drive, and that is
    /// the second attempt at this - the first moved a car off entrant zero and
    /// it still went nowhere.
    ///
    /// The game's own model is the reason. It knows player one, player two and
    /// COM, which is what its memory editor offers, so entrants zero and one
    /// are both people's seats: a car moved from the first to the second was
    /// moved from a pad nobody was holding to a second pad nobody was holding.
    /// Only entrant two and beyond are the game's to drive.
    ///
    /// So a room of two becomes a race of four - the two seats for people, left
    /// with whatever the arcade put in them, and the room's own cars behind.
    /// Two extra cars in a test race is a cost worth paying to have the test
    /// run itself.
    /// </summary>
    public const int PeopleSeats = 2;

    public static int FirstRoomEntrant => AiDrives ? PeopleSeats : 0;

    static byte AiSkillWanted =>
        byte.TryParse(AiAsked, out byte skill) ? skill : DefaultAiSkill;

    const byte DefaultAiSkill = 100;

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

        int racing = RoomSize(drivers);
        int from = FirstRoomEntrant;
        m.WriteU8(Block + Count, (byte)(from + racing));

        // The human always drives entrant 0 - proven by putting the local
        // player anywhere else and watching them drive entrant 0's car
        // regardless of which entrant was marked. So each machine leads with
        // its own player, and the entrant order differs from machine to
        // machine by exactly that rotation.
        var order = Order(drivers, leader, racing);

        for (int i = 0; i < racing; i++)
        {
            var player = order[i];
            uint entrant = Block + (uint)(FirstEntrant + (from + i) * EntrantSize);

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

            // Entrant 0 is this machine's own, and the only one a person
            // drives - unless the game has been asked to drive it too.
            bool human = i == 0 && !AiDrives;
            m.WriteU8(entrant + IsAi, (byte)(human ? 0 : 1));
            m.WriteU8(entrant + AiSkill, human ? (byte)0 : AiSkillWanted);

            // The place comes from the room, not from the entrant slot. Slots
            // are rotated so each machine drives its own car, but the room's
            // order is the same everywhere - so every machine puts every
            // player in the same place on the grid, however it numbers them.
            //
            // And this is the whole of what puts a car on a square: four
            // machines report the same coordinates for the same number here,
            // in four different rotations.
            int place = drivers.Take(racing).ToList().FindIndex(p => p.Name == player.Name);
            m.WriteU8(entrant + GridPlace, (byte)(from + (place < 0 ? i : place)));
        }

        // And the cars nobody in the room is driving take the places nobody in
        // the room is using - when GT2_GRID_LEFTOVERS asks for it.
        //
        // The reasoning was that the block always has six entrants, that the
        // arcade numbers its own six 5 down to 0, and that writing places for
        // only the room's four left entrants four and five holding 1 and 0 -
        // colliding with the room's second and first.
        //
        // The reasoning was wrong, or +0x8D is not what places a car: with it
        // on, all six cars started on the same square, which is worse than the
        // collision it was meant to remove. Off by default until the report
        // below says what the game does with these numbers.
        // The seats for people go at the front of the grid when there are any,
        // because +0x8D is what stands a car on a square and two entrants
        // holding the arcade's own numbers would stand somebody on top of a
        // car from the room.
        for (int i = 0; i < from; i++)
        {
            uint seat = Block + (uint)(FirstEntrant + i * EntrantSize);
            m.WriteU8(seat + GridPlace, (byte)i);
            m.WriteU8(seat + IsAi, 1);
            m.WriteU8(seat + AiSkill, AiSkillWanted);
        }

        if (PlaceTheLeftovers)
            for (int i = from + racing; i < Slots; i++)
                m.WriteU8(Block + (uint)(FirstEntrant + i * EntrantSize) + GridPlace, (byte)i);

        Say(m, drivers, leader, order, cars);
        return true;
    }

    /// <summary>
    /// Says what went on the grid, once, per machine.
    ///
    /// Four players desynchronised where two had not, and three of the four
    /// symptoms - two cars on the same grid slot, cars nobody but their owner
    /// could see, every car in one colour - are all the shape of machines
    /// disagreeing about the room. A line per machine naming the drivers, their
    /// seats, their slots, their cars and their paints is what turns that from
    /// a guess into a comparison.
    /// </summary>
    static void Say(IMemory m, IReadOnlyList<Player> drivers, string leader,
                    List<Player> order, CarCatalogue? cars)
    {
        var said = new System.Text.StringBuilder();
        for (int i = 0; i < order.Count; i++)
        {
            int slot = i + FirstRoomEntrant;
            var player = order[i];
            int seat = drivers.ToList().FindIndex(p => p.Name == player.Name);
            var paints = cars?.Colours(player.Car);
            string paint = PaintFor(cars, player) is { } letter
                ? $"'{(char)letter}'"
                : $"none (of {paints?.Count ?? 0})";
            byte place = m.ReadU8(Block + (uint)(FirstEntrant + slot * EntrantSize) + GridPlace);
            said.Append($"{Environment.NewLine}[grid]   slot {slot} seat {seat} place {place}"
                + $"  {player.Name}  {player.Car}  colour {player.Colour} -> {paint}");
        }

        for (int slot = order.Count + FirstRoomEntrant; slot < Slots; slot++)
            said.Append($"{Environment.NewLine}[grid]   slot {slot} place "
                + m.ReadU8(Block + (uint)(FirstEntrant + slot * EntrantSize) + GridPlace)
                + "  (not in the room)");

        Console.Error.WriteLine(
            $"[grid] {drivers.Count} driver(s), led by {leader}"
            + (AiDrives ? $", driven by the game at skill {AiSkillWanted}" : "")
            + $", leftovers {(PlaceTheLeftovers ? "renumbered" : "left alone")}:" + said);
    }

    /// <summary>
    /// The place the block now holds for a slot. Public because the question of
    /// what the game stands a car by - the slot it is in or the number written
    /// in it - is answered by reading both and comparing.
    /// </summary>
    public static byte PlaceOf(IMemory m, int slot) =>
        m.ReadU8(Block + (uint)(FirstEntrant + slot * EntrantSize) + GridPlace);

    static void WriteText(IMemory m, uint at, string text, int room)
    {
        for (int i = 0; i < room; i++)
            m.WriteU8(at + (uint)i, i < text.Length && text[i] < 0x80 ? (byte)text[i] : (byte)0);
    }
}
