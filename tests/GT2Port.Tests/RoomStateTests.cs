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
        // Either it is rejected, or it happens to parse - it must not throw.
        RoomState.TryDeserialise(junk, out _);
    }

    [Fact]
    public void Fits_a_full_room_in_one_datagram()
    {
        var full = Sample() with
        {
            Players = [.. Enumerable.Range(0, 6)
                .Select(i => new Player($"player{i}", "Some Long Car Name GT-R V-Spec", true))],
        };
        // Whole state is retransmitted on every change; it has to fit one packet.
        Assert.True(RoomState.Serialise(full).Length < 1200);
    }
}
