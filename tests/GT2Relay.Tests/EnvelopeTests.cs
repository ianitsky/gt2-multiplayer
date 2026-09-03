using System.Net;
using GT2Port.Rendezvous;
using Xunit;

namespace GT2Relay.Tests;

/// <summary>
/// The format between a player and the rendezvous server.
///
/// Every one of these arrives off the open internet, so the shape that matters
/// is not "does a good message survive" but "does a bad one get refused" - a
/// length field that promises more bytes than the datagram carries is the
/// oldest way to turn a parser into a crash.
/// </summary>
public class EnvelopeTests
{
    static readonly Guid Room = Guid.Parse("11111111-2222-3333-4444-555555555555");
    static readonly IPEndPoint Somewhere = new(IPAddress.Parse("203.0.113.7"), 51234);

    [Fact]
    public void Round_trips_a_publish()
    {
        byte[] card = [1, 2, 3, 4, 5];

        var bytes = Envelope.WritePublish(Room, listed: true, card);

        Assert.True(Envelope.TryReadKind(bytes, out var kind));
        Assert.Equal(Envelope.Kind.Publish, kind);
        Assert.True(Envelope.TryReadPublish(bytes, out var room, out bool listed, out var back));
        Assert.Equal(Room, room);
        Assert.True(listed);
        Assert.Equal(card, back);
    }

    [Fact]
    public void Round_trips_an_unlisted_publish()
    {
        var bytes = Envelope.WritePublish(Room, listed: false, [9]);

        Assert.True(Envelope.TryReadPublish(bytes, out _, out bool listed, out _));
        Assert.False(listed);
    }

    [Fact]
    public void Round_trips_a_room_card()
    {
        byte[] card = [7, 7, 7];

        var bytes = Envelope.WriteRoomCard(Room, "AB23CD", card);

        Assert.True(Envelope.TryReadRoomCard(bytes, out var room, out string code, out var back));
        Assert.Equal(Room, room);
        Assert.Equal("AB23CD", code);
        Assert.Equal(card, back);
    }

    [Fact]
    public void Round_trips_a_peer()
    {
        var bytes = Envelope.WritePeer(Room, Somewhere);

        Assert.True(Envelope.TryReadPeer(bytes, out var room, out var peer));
        Assert.Equal(Room, room);
        Assert.Equal(Somewhere, peer);
    }

    [Fact]
    public void Round_trips_a_relay_and_what_it_becomes()
    {
        byte[] payload = [0xA5, 3, 4, 5];

        var out_ = Envelope.WriteRelay(Room, Somewhere, payload);
        Assert.True(Envelope.TryReadRelay(out_, out var room, out var to, out var sent));
        Assert.Equal(Room, room);
        Assert.Equal(Somewhere, to);
        Assert.Equal(payload, sent);

        var back = Envelope.WriteRelayed(Room, Somewhere, sent);
        Assert.True(Envelope.TryReadRelayed(back, out var room2, out var from, out var got));
        Assert.Equal(Room, room2);
        Assert.Equal(Somewhere, from);
        Assert.Equal(payload, got);
    }

    [Fact]
    public void Round_trips_the_short_messages()
    {
        Assert.True(Envelope.TryReadJoin(Envelope.WriteJoin(Room), out var a));
        Assert.Equal(Room, a);

        Assert.True(Envelope.TryReadJoined(Envelope.WriteJoined(Room), out var b));
        Assert.Equal(Room, b);

        Assert.True(Envelope.TryReadLeave(Envelope.WriteLeave(Room), out var c));
        Assert.Equal(Room, c);

        Assert.True(Envelope.TryReadJoinByCode(Envelope.WriteJoinByCode("HJ4KMN"), out string code));
        Assert.Equal("HJ4KMN", code);

        Assert.True(Envelope.TryReadKind(Envelope.WriteList(), out var list));
        Assert.Equal(Envelope.Kind.List, list);

        Assert.True(Envelope.TryReadKind(Envelope.WriteNoRoom(), out var none));
        Assert.Equal(Envelope.Kind.NoRoom, none);
    }

    [Fact]
    public void Refuses_a_datagram_that_is_not_ours()
    {
        Assert.False(Envelope.TryReadKind([0xA5, 1, 1], out _));
    }

    [Fact]
    public void Refuses_a_version_it_does_not_know()
    {
        Assert.False(Envelope.TryReadKind([Envelope.Magic, 99, 1], out _));
    }

    [Fact]
    public void Refuses_an_empty_datagram()
    {
        Assert.False(Envelope.TryReadKind([], out _));
        Assert.False(Envelope.TryReadPublish([], out _, out _, out _));
    }

    /// <summary>
    /// A length that promises more than the datagram holds. This is the case
    /// that turns a parser into a crash, and it is the one a hostile sender
    /// reaches for first.
    /// </summary>
    [Fact]
    public void Refuses_a_card_longer_than_the_datagram()
    {
        var bytes = Envelope.WritePublish(Room, listed: true, [1, 2, 3, 4]);
        var truncated = bytes[..^2];

        Assert.False(Envelope.TryReadPublish(truncated, out _, out _, out _));
    }

    [Fact]
    public void Refuses_a_relay_payload_longer_than_the_datagram()
    {
        var bytes = Envelope.WriteRelay(Room, Somewhere, [1, 2, 3, 4]);

        Assert.False(Envelope.TryReadRelay(bytes[..^1], out _, out _, out _));
    }

    [Fact]
    public void Refuses_to_write_more_than_it_will_carry()
    {
        Assert.Throws<ArgumentException>(
            () => Envelope.WritePublish(Room, true, new byte[Envelope.MaxCard + 1]));
        Assert.Throws<ArgumentException>(
            () => Envelope.WriteRelay(Room, Somewhere, new byte[Envelope.MaxPayload + 1]));
    }

    /// <summary>
    /// Reading one kind out of another's bytes must fail rather than return
    /// something plausible: every kind is a different shape and a parser that
    /// ignores the kind byte will read a room id out of a payload.
    /// </summary>
    [Fact]
    public void Refuses_to_read_one_kind_as_another()
    {
        var join = Envelope.WriteJoin(Room);

        Assert.False(Envelope.TryReadPublish(join, out _, out _, out _));
        Assert.False(Envelope.TryReadRelayed(join, out _, out _, out _));
    }

    [Fact]
    public void Refuses_a_code_that_is_the_wrong_length()
    {
        Assert.Throws<ArgumentException>(() => Envelope.WriteJoinByCode("ABC"));
    }
}
