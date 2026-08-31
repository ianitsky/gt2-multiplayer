using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class RoomStateTests
{
    static Room Sample() => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        "Ian's room", "Trial Mountain", "special", 6,
        [new Player("ian", "Skyline", true), new Player("guest", "Supra", false)]);

    [Fact]
    public void Round_trips_a_room()
    {
        Assert.True(RoomState.TryDeserialise(RoomState.Serialise(Sample()), out var back));

        var original = Sample();
        Assert.Equal(original.Id, back.Id);
        Assert.Equal(original.Name, back.Name);
        Assert.Equal(original.Track, back.Track);
        Assert.Equal(original.CarGroup, back.CarGroup);
        Assert.Equal(original.MaxPlayers, back.MaxPlayers);
        Assert.Equal(original.Players.Count, back.Players.Count);
    }

    /// <summary>
    /// The lap count is the host's choice and every other machine reads it off
    /// the room, so a race whose length did not survive the wire would be six
    /// machines running six different races.
    /// </summary>
    [Fact]
    public void Round_trips_how_many_laps_the_race_is()
    {
        var room = Sample() with { Laps = 47 };
        Assert.True(RoomState.TryDeserialise(RoomState.Serialise(room), out var back));
        Assert.Equal(47, back.Laps);
    }

    /// <summary>
    /// A room arrives off a socket, so its lap count is clamped rather than
    /// trusted: zero laps is a race that ends before it starts.
    ///
    /// Which byte carries it is found by asking the format rather than by
    /// counting through it - two rooms alike but for their laps differ in
    /// exactly one place.
    /// </summary>
    [Fact]
    public void A_lap_count_off_the_wire_is_clamped()
    {
        var one = RoomState.Serialise(Sample() with { Laps = 1 });
        var two = RoomState.Serialise(Sample() with { Laps = 2 });

        var differ = Enumerable.Range(0, one.Length).Where(i => one[i] != two[i]).ToList();
        int at = Assert.Single(differ);

        one[at] = 0;
        Assert.True(RoomState.TryDeserialise(one, out var back));
        Assert.Equal(RaceLaps.Fewest, back.Laps);
    }

    [Fact]
    public void Round_trips_every_player_field()
    {
        RoomState.TryDeserialise(RoomState.Serialise(Sample()), out var back);

        Assert.Equal("ian", back.Players[0].Name);
        Assert.Equal("Skyline", back.Players[0].Car);
        Assert.True(back.Players[0].Ready);
        Assert.False(back.Players[1].Ready);
    }

    [Fact]
    public void Round_trips_a_room_with_no_players()
    {
        var empty = Sample() with { Players = [] };
        Assert.True(RoomState.TryDeserialise(RoomState.Serialise(empty), out var back));
        Assert.Empty(back.Players);
    }

    [Fact]
    public void Rejects_a_truncated_packet_without_throwing()
    {
        var data = RoomState.Serialise(Sample());
        Assert.False(RoomState.TryDeserialise(data.AsSpan(0, data.Length / 2), out _));
    }

    [Fact]
    public void Rejects_an_empty_packet()
    {
        Assert.False(RoomState.TryDeserialise([], out _));
    }

    [Fact]
    public void Rejects_corrupted_packet_while_preserving_invariants()
    {
        // Build a minimal packet with 0 players, then append 7 minimal valid players
        var empty = new Room(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "", "", "", 6, []);

        var data = RoomState.Serialise(empty).ToList();
        // Packet is now: version(1) + guid(16) + name_len(1) + track_len(1) + carGroup_len(1) + maxPlayers(1) + count(1)
        // = 1 + 16 + 1 + 1 + 1 + 1 + 1 = 22 bytes, with count at byte 21

        // The count byte is at index data.Count - 1
        int countByteIndex = data.Count - 1;

        // Append 7 minimal players (5 bytes each: name_len(1) + name(1) + car_len(1) + car(1) + ready(1))
        for (int i = 0; i < 7; i++)
        {
            data.AddRange(new byte[] { 1, (byte)('a' + i), 1, (byte)('c' + i), 1 });
        }

        // Change count from 0 to 7
        data[countByteIndex] = 7;

        // A count above MaxPlayers must be rejected outright, not merely
        // parsed-then-checked - no assertion here may sit behind an `if`
        // that decides whether it runs.
        Assert.False(RoomState.TryDeserialise([.. data], out _));
    }

    // ---- Task 9 review, Minor 6: TryDeserialise must bound maxPlayers too ----

    [Fact]
    public void Rejects_a_forged_maxPlayers_above_the_cap()
    {
        var room = new Room(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "", "", "", 6, []);

        var data = RoomState.Serialise(room).ToList();

        // Found by asking the format rather than by counting through it: two
        // rooms alike but for their player limit differ in exactly one byte.
        // Counting was how this test broke when the laps byte was added
        // between the limit and the player count.
        var five = RoomState.Serialise(room with { MaxPlayers = 5 });
        int maxPlayersByteIndex = Assert.Single(
            Enumerable.Range(0, data.Count).Where(i => data[i] != five[i]));

        // Positive precondition: the untouched packet round-trips, so the
        // rejection below is the forged byte's doing, not a broken parser
        // (Finding 1).
        Assert.True(RoomState.TryDeserialise([.. data], out var back));
        Assert.Equal(6, back.MaxPlayers);

        // A forged or corrupt maxPlayers above the cap must be rejected
        // outright - LanDiscovery stores it raw, and the room list would
        // otherwise render an impossible "1/255" as joinable.
        data[maxPlayersByteIndex] = 255;
        Assert.False(RoomState.TryDeserialise([.. data], out _));
    }

    [Fact]
    public void Fits_a_full_room_in_one_datagram()
    {
        // Every string here is long enough to hit the truncation cap, whatever that
        // cap currently is - this is the true worst case, not a stand-in for it.
        var longString = new string('x', RoomState.MaxStringBytes);
        var full = new Room(
            Guid.NewGuid(), longString, longString, longString, RoomState.MaxPlayers,
            [.. Enumerable.Range(0, RoomState.MaxPlayers)
                .Select(_ => new Player(longString, longString, true))]);

        // Whole state is retransmitted on every change; it has to fit one packet.
        Assert.True(RoomState.Serialise(full).Length < 1200);
    }

    [Fact]
    public void Serialising_seven_players_throws()
    {
        var room = Sample() with
        {
            Players = [.. Enumerable.Range(0, 7).Select(i => new Player($"p{i}", "car", true))],
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomState.Serialise(room));
    }

    [Fact]
    public void Serialising_exactly_six_players_does_not_throw()
    {
        var room = Sample() with
        {
            Players = [.. Enumerable.Range(0, 6).Select(i => new Player($"p{i}", "car", true))],
        };
        var bytes = RoomState.Serialise(room);
        Assert.NotEmpty(bytes);
    }

    [Fact]
    public void Serialising_negative_max_players_throws()
    {
        var room = Sample() with { MaxPlayers = -1 };
        Assert.Throws<ArgumentOutOfRangeException>(() => RoomState.Serialise(room));
    }

    [Fact]
    public void Truncates_multibyte_strings_on_a_character_boundary()
    {
        // Each '日' is 3 UTF-8 bytes; 40 of them is 120 bytes, well past the 64-byte cap,
        // and not an even multiple of 3 bytes past it, so a flat byte-cut would split one.
        var longName = new string('日', 40);
        var room = Sample() with { Name = longName };

        Assert.True(RoomState.TryDeserialise(RoomState.Serialise(room), out var back));

        Assert.DoesNotContain('�', back.Name);
        Assert.StartsWith(back.Name, longName);
        Assert.NotEmpty(back.Name);
    }

    [Fact]
    public void Round_trips_the_car_group()
    {
        var room = new Room(Guid.NewGuid(), "Room", "2p_mountain", "special", 6,
            [new Player("ian", "dvpgn", true)]);

        Assert.True(RoomState.TryDeserialise(RoomState.Serialise(room), out var back));

        Assert.Equal("special", back.CarGroup);
        Assert.Equal("dvpgn", back.Players[0].Car);
    }

    [Fact]
    public void Rejects_a_packet_from_the_older_format()
    {
        var room = new Room(Guid.NewGuid(), "Room", "2p_mountain", "special", 6, []);
        var packet = RoomState.Serialise(room);

        // Positive precondition: the untouched packet round-trips, so the
        // rejection below is the forged version byte's doing, not a broken
        // parser (Finding 1).
        Assert.True(RoomState.TryDeserialise(packet, out _));

        packet[0] = 1;

        Assert.False(RoomState.TryDeserialise(packet, out _));
    }

    /// <summary>
    /// The paint has to survive the wire or the lobby is choosing a colour
    /// only the chooser can see, which is the one thing it exists not to do.
    /// </summary>
    [Fact]
    public void Round_trips_the_paint_each_player_chose()
    {
        var room = new Room(Guid.NewGuid(), "room", "parma_2p", "special", 6,
            [new Player("ian", "dvpgn", true, Colour: 2),
             new Player("guest", "buc9n", false, Colour: 11)]);

        Assert.True(RoomState.TryDeserialise(RoomState.Serialise(room), out var back));

        Assert.Equal([(byte)2, (byte)11], back.Players.Select(p => p.Colour));
    }

    /// <summary>
    /// Whether a player is racing or watching has to survive the wire: it is
    /// what every machine derives the seats from, and two machines disagreeing
    /// about it would key the same car differently.
    /// </summary>
    [Fact]
    public void Round_trips_who_is_only_watching()
    {
        var room = new Room(Guid.NewGuid(), "room", "parma_2p", "special", 6,
            [new Player("ian", "dvpgn", true),
             new Player("guest", "", true, Colour: 0, Watching: true)]);

        Assert.True(RoomState.TryDeserialise(RoomState.Serialise(room), out var back));

        Assert.Equal([false, true], back.Players.Select(p => p.Watching));
    }
}
