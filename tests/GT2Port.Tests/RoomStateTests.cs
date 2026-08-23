using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class RoomStateTests
{
    static Room Sample() => new(
        Guid.Parse("11111111-2222-3333-4444-555555555555"),
        "Ian's room", "Trial Mountain", 6,
        [new Player("ian", "Skyline", true), new Player("guest", "Supra", false)]);

    [Fact]
    public void Round_trips_a_room()
    {
        Assert.True(RoomState.TryDeserialise(RoomState.Serialise(Sample()), out var back));

        var original = Sample();
        Assert.Equal(original.Id, back.Id);
        Assert.Equal(original.Name, back.Name);
        Assert.Equal(original.Track, back.Track);
        Assert.Equal(original.MaxPlayers, back.MaxPlayers);
        Assert.Equal(original.Players.Count, back.Players.Count);
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
            "", "", 6, []);

        var data = RoomState.Serialise(empty).ToList();
        // Packet is now: version(1) + guid(16) + name_len(1) + track_len(1) + maxPlayers(1) + count(1)
        // = 1 + 16 + 1 + 1 + 1 + 1 = 21 bytes, with count at byte 20

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
            "", "", 6, []);

        var data = RoomState.Serialise(room).ToList();
        // Same layout as Rejects_corrupted_packet_while_preserving_invariants:
        // version(1) + guid(16) + name_len(1) + track_len(1) + maxPlayers(1) + count(1)
        // - maxPlayers sits right before count, at data.Count - 2.
        int maxPlayersByteIndex = data.Count - 2;

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
            Guid.NewGuid(), longString, longString, RoomState.MaxPlayers,
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
}
