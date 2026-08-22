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
    public void Rejects_garbage_without_throwing()
    {
        var junk = new byte[64];
        Random.Shared.NextBytes(junk);

        // The call completing at all (rather than throwing) is what protects the
        // socket-reading caller; xunit fails this test automatically if it throws.
        bool parsed = RoomState.TryDeserialise(junk, out var room);

        // If it did happen to parse, the result must still respect the wire format's
        // own invariants rather than passing raw garbage through as a "room".
        if (parsed)
        {
            Assert.InRange(room.Players.Count, 0, RoomState.MaxPlayers);
        }
    }

    [Fact]
    public void Fits_a_full_room_in_one_datagram()
    {
        // Every string here is long enough to hit the truncation cap, whatever that
        // cap currently is - this is the true worst case, not a stand-in for it.
        var longString = new string('x', 200);
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
