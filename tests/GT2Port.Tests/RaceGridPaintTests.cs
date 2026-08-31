using System.Text;
using GT2Port.Multiplayer;
using RecompOne.Runtime.Memory;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// The paint a player chose in the lobby has to reach the race on every
/// machine, not only on theirs. Each machine writes all six entrants, so what
/// these check is that the entrant carries the letter the car's own list names
/// the paint by - which is what
/// gt2_ovr3_build_race_block_and_fill_all_six_entrants puts there when a
/// player walks the menus, sign-extended into the word at +0x04.
/// </summary>
public class RaceGridPaintTests
{
    const uint Block = 0x801D585Cu;
    const uint FirstEntrant = 0x5Cu;
    const uint EntrantSize = 0xD0u;
    const uint PaintLetter = 0x04u;

    const string Alphabet = "-0123456789abcdefghijklmnopqrstuvwxyz";

    static uint Pack(string code)
    {
        uint packed = 0;
        for (int i = 0; i < 5; i++)
            packed |= (uint)Alphabet.IndexOf(code[i]) << ((4 - i) * 6);
        return packed;
    }

    /// <summary>
    /// A one-car database whose block begins with paints, the way the disc's
    /// blocks do: the swatches as halfwords, a letter each, then the counted
    /// name.
    /// </summary>
    static CarCatalogue CatalogueWith(string code, params char[] letters)
    {
        var block = new List<byte>();
        foreach (char _ in letters) block.AddRange(BitConverter.GetBytes((ushort)0x1234));
        foreach (char letter in letters) block.Add((byte)letter);

        var text = Encoding.ASCII.GetBytes("Car " + code);
        block.Add((byte)text.Length);
        block.AddRange(text);
        block.Add(0);

        var image = new List<byte>(Encoding.ASCII.GetBytes("CAR\0"));
        image.AddRange(BitConverter.GetBytes(1u));
        image.AddRange(BitConverter.GetBytes(Pack(code)));
        image.AddRange(BitConverter.GetBytes(16u | ((uint)(letters.Length - 1) << 18)));
        image.AddRange(block);

        return CarCatalogue.FromJson(CarInfo.TryParse(image.ToArray()), null);
    }

    /// <summary>
    /// A race the menus have finished building, which is what TryApply waits
    /// for: six entrants, each already naming some car.
    /// </summary>
    static IMemory BuiltRace()
    {
        var m = new PSMemory();
        for (uint i = 0; i < RaceGrid.Slots; i++)
            m.WriteU32(Block + FirstEntrant + i * EntrantSize, Pack("us36n"));
        return m;
    }

    static uint PaintIn(IMemory m, int entrant) =>
        m.ReadU32(Block + FirstEntrant + (uint)entrant * EntrantSize + PaintLetter);

    [Fact]
    public void Writes_the_letter_the_cars_own_list_names_the_paint_by()
    {
        var m = BuiltRace();
        var cars = CatalogueWith("dvpgn", '4', '6', 'b');

        Assert.True(RaceGrid.TryApply(
            m, [new Player("ian", "dvpgn", true, Colour: 2)], "ian", cars));

        Assert.Equal((uint)'b', PaintIn(m, 0));
    }

    /// <summary>
    /// The whole point: every machine paints every car, so a client writes the
    /// host's paint as readily as its own. The order rotates - each machine
    /// leads with its own player - so the paint follows the player, not the
    /// slot.
    /// </summary>
    [Fact]
    public void Paints_every_car_in_the_room_and_not_only_this_ones()
    {
        var m = BuiltRace();
        var cars = CatalogueWith("dvpgn", '4', '6', 'b');

        List<Player> room =
        [
            new Player("ian", "dvpgn", true, Colour: 0),
            new Player("guest", "dvpgn", true, Colour: 2),
        ];

        // Written from the client's machine, so "guest" leads.
        Assert.True(RaceGrid.TryApply(m, room, "guest", cars));

        Assert.Equal((uint)'b', PaintIn(m, 0));
        Assert.Equal((uint)'4', PaintIn(m, 1));
    }

    /// <summary>
    /// A letter is a signed byte where it comes from, and the entrant field is
    /// a word - the game sign-extends. Nothing on the disc uses a letter above
    /// 0x7F today, so this pins the rule rather than a case that has been
    /// seen.
    /// </summary>
    [Fact]
    public void Sign_extends_a_letter_past_the_top_of_the_byte()
    {
        var m = BuiltRace();
        var cars = CatalogueWith("dvpgn", (char)0x80);

        Assert.True(RaceGrid.TryApply(
            m, [new Player("ian", "dvpgn", true, Colour: 0)], "ian", cars));

        Assert.Equal(0xFFFFFF80u, PaintIn(m, 0));
    }

    /// <summary>
    /// Without a car database there is no letter to write, and the entrant
    /// keeps what the arcade left there - which is a paint the car really has.
    /// Writing a zero instead would name no paint at all.
    /// </summary>
    [Fact]
    public void Leaves_the_arcades_paint_alone_when_the_disc_cannot_say()
    {
        var m = BuiltRace();
        m.WriteU32(Block + FirstEntrant + PaintLetter, (uint)'q');

        Assert.True(RaceGrid.TryApply(
            m, [new Player("ian", "dvpgn", true, Colour: 0)], "ian", cars: null));

        Assert.Equal((uint)'q', PaintIn(m, 0));
    }

    /// <summary>
    /// The same, for a paint number the car does not have - which a room can
    /// hold for one frame while a car change and a colour change cross on the
    /// wire.
    /// </summary>
    [Fact]
    public void Leaves_the_arcades_paint_alone_when_the_car_has_no_such_paint()
    {
        var m = BuiltRace();
        m.WriteU32(Block + FirstEntrant + PaintLetter, (uint)'q');
        var cars = CatalogueWith("dvpgn", '4', '6', 'b');

        Assert.True(RaceGrid.TryApply(
            m, [new Player("ian", "dvpgn", true, Colour: 7)], "ian", cars));

        Assert.Equal((uint)'q', PaintIn(m, 0));
    }

    const uint GridPlace = 0x8Du;

    static byte PlaceIn(IMemory m, int entrant) =>
        m.ReadU8(Block + FirstEntrant + (uint)entrant * EntrantSize + GridPlace);

    /// <summary>
    /// The entrants nobody in the room is driving keep what the arcade left
    /// them, which is what running with them renumbered showed was right: with
    /// every entrant given a number of its own, all six cars started on the
    /// same square. Whatever +0x8D is, it is not a square to stand on.
    /// </summary>
    [Fact]
    public void The_entrants_nobody_is_driving_keep_the_arcades_numbers()
    {
        var m = BuiltRace();
        for (uint i = 0; i < RaceGrid.Slots; i++)
            m.WriteU8(Block + FirstEntrant + i * EntrantSize + GridPlace,
                      (byte)(RaceGrid.Slots - 1 - i));

        List<Player> room =
        [
            new Player("ian", "dvpgn", true),
            new Player("les", "dvpgn", true),
        ];

        Assert.True(RaceGrid.TryApply(m, room, "ian", cars: null));

        // The room's two are numbered by the room...
        Assert.Equal(0, PlaceIn(m, 0));
        Assert.Equal(1, PlaceIn(m, 1));

        // ...and the other four are left exactly as they were found.
        Assert.Equal([3, 2, 1, 0], Enumerable.Range(2, 4).Select(i => (int)PlaceIn(m, i)));
    }

    /// <summary>
    /// And the room's own players keep the places the room gives them, so every
    /// machine agrees on who starts where however it rotates its entrants.
    /// </summary>
    [Fact]
    public void The_rooms_order_is_the_grid_order_on_every_machine()
    {
        List<Player> room =
        [
            new Player("ian", "dvpgn", true),
            new Player("les", "dvpgn", true),
            new Player("kay", "dvpgn", true),
        ];

        var mine = BuiltRace();
        var theirs = BuiltRace();
        Assert.True(RaceGrid.TryApply(mine, room, "ian", cars: null));
        Assert.True(RaceGrid.TryApply(theirs, room, "kay", cars: null));

        // "kay" is the room's third, so both machines start it third - on one
        // it is entrant 2, on the other it is entrant 0.
        Assert.Equal(2, PlaceIn(mine, 2));
        Assert.Equal(2, PlaceIn(theirs, 0));
    }
}
