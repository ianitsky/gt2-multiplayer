using System.Net;
using GT2Port.Rendezvous;
using Xunit;

namespace GT2Relay.Tests;

/// <summary>
/// Everything the server decides.
///
/// The clock is injected so expiry is a line of test rather than half a minute
/// of waiting, and the Random is seeded so a code is something a test can name.
/// Nothing here opens a socket: the loop that does is Task 3, and it should
/// have no decisions left in it.
/// </summary>
public class RoomRegistryTests
{
    DateTime _now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
    void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    RoomRegistry New() => new(() => _now, new Random(1234));

    static readonly Guid Room = Guid.Parse("11111111-2222-3333-4444-555555555555");
    static readonly IPEndPoint Host = new(IPAddress.Parse("198.51.100.10"), 40000);
    static readonly IPEndPoint Guest = new(IPAddress.Parse("203.0.113.20"), 50000);
    static readonly IPEndPoint Third = new(IPAddress.Parse("203.0.113.21"), 50001);

    static byte[] Card(params byte[] bytes) => bytes;

    static List<Reply> Of(IReadOnlyList<Reply> replies, Envelope.Kind kind) =>
        [.. replies.Where(r => Envelope.TryReadKind(r.Data, out var k) && k == kind)];

    string Publish(RoomRegistry registry, Guid room, bool listed = true, IPEndPoint? host = null)
    {
        var replies = registry.Heard(host ?? Host,
            Envelope.WritePublish(room, listed, Card(1, 2, 3)));
        var published = Of(replies, Envelope.Kind.Published);
        Assert.Single(published);
        Assert.True(Envelope.TryReadPublished(published[0].Data, out _, out string code));
        return code;
    }

    [Fact]
    public void Publishing_a_room_answers_with_a_code()
    {
        var registry = New();

        string code = Publish(registry, Room);

        Assert.True(RoomCode.IsWellFormed(code));
        Assert.Equal(1, registry.RoomCount);
        Assert.True(registry.TryFindByCode(code, out var found));
        Assert.Equal(Room, found);
    }

    /// <summary>
    /// The host publishes on a timer, and every publish is also the keepalive
    /// that says the room is still there. A second publish must not mint a
    /// second code - the one on the host's screen has to keep working.
    /// </summary>
    [Fact]
    public void Publishing_again_keeps_the_same_code()
    {
        var registry = New();

        string first = Publish(registry, Room);
        Advance(5);
        string again = Publish(registry, Room);

        Assert.Equal(first, again);
        Assert.Equal(1, registry.RoomCount);
    }

    [Fact]
    public void A_listed_room_is_handed_out_and_an_unlisted_one_is_not()
    {
        var registry = New();
        Publish(registry, Room, listed: true);
        var quiet = Guid.NewGuid();
        Publish(registry, quiet, listed: false, host: Third);

        var cards = Of(registry.Heard(Guest, Envelope.WriteList()), Envelope.Kind.RoomCard);

        Assert.Single(cards);
        Assert.True(Envelope.TryReadRoomCard(cards[0].Data, out var id, out _, out var card));
        Assert.Equal(Room, id);
        Assert.Equal(Card(1, 2, 3), card);
    }

    /// <summary>
    /// A room nobody listed is still a room somebody was told the code of.
    /// </summary>
    [Fact]
    public void An_unlisted_room_can_still_be_joined_by_code()
    {
        var registry = New();
        string code = Publish(registry, Room, listed: false);

        var replies = registry.Heard(Guest, Envelope.WriteJoinByCode(code));

        Assert.Single(Of(replies, Envelope.Kind.Joined));
    }

    /// <summary>
    /// Joining is where the two sides learn each other's public address. Both
    /// have to be told, and each has to be told about the other rather than
    /// about itself - which is the mistake that makes a room where everyone
    /// talks to themselves.
    /// </summary>
    [Fact]
    public void Joining_tells_each_side_where_the_other_is()
    {
        var registry = New();
        Publish(registry, Room);

        var replies = registry.Heard(Guest, Envelope.WriteJoin(Room));

        var joined = Of(replies, Envelope.Kind.Joined);
        Assert.Single(joined);
        Assert.Equal(Guest, joined[0].To);

        var peers = Of(replies, Envelope.Kind.Peer);
        Assert.Equal(2, peers.Count);

        var toGuest = peers.Single(p => p.To.Equals(Guest));
        Assert.True(Envelope.TryReadPeer(toGuest.Data, out _, out var guestWasTold));
        Assert.Equal(Host, guestWasTold);

        var toHost = peers.Single(p => p.To.Equals(Host));
        Assert.True(Envelope.TryReadPeer(toHost.Data, out _, out var hostWasTold));
        Assert.Equal(Guest, hostWasTold);
    }

    [Fact]
    public void Joining_a_room_that_is_not_there_says_so()
    {
        var registry = New();

        var replies = registry.Heard(Guest, Envelope.WriteJoin(Guid.NewGuid()));

        Assert.Single(Of(replies, Envelope.Kind.NoRoom));
    }

    [Fact]
    public void A_code_that_names_nothing_says_so()
    {
        var registry = New();

        var replies = registry.Heard(Guest, Envelope.WriteJoinByCode("ZZZZZZ"));

        Assert.Single(Of(replies, Envelope.Kind.NoRoom));
    }

    [Fact]
    public void Relaying_carries_the_payload_to_the_other_side_saying_who_sent_it()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        byte[] payload = [0xA5, 1, 2, 3];
        var replies = registry.Heard(Guest, Envelope.WriteRelay(Room, Host, payload));

        var relayed = Of(replies, Envelope.Kind.Relayed);
        Assert.Single(relayed);
        Assert.Equal(Host, relayed[0].To);
        Assert.True(Envelope.TryReadRelayed(relayed[0].Data, out var room, out var from, out var got));
        Assert.Equal(Room, room);
        Assert.Equal(Guest, from);
        Assert.Equal(payload, got);
    }

    /// <summary>
    /// Otherwise the server is an open reflector: anybody could aim a payload
    /// at any address on the internet and have it arrive from here.
    /// </summary>
    [Fact]
    public void A_stranger_cannot_relay_through_a_room_it_never_joined()
    {
        var registry = New();
        Publish(registry, Room);

        var replies = registry.Heard(Third, Envelope.WriteRelay(Room, Host, [1, 2, 3]));

        Assert.Empty(replies);
    }

    [Fact]
    public void And_a_member_cannot_relay_to_an_address_outside_its_room()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        var outside = new IPEndPoint(IPAddress.Parse("192.0.2.99"), 9999);
        var replies = registry.Heard(Guest, Envelope.WriteRelay(Room, outside, [1, 2, 3]));

        Assert.Empty(replies);
    }

    [Fact]
    public void Leaving_takes_a_member_out_of_the_room()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        registry.Heard(Guest, Envelope.WriteLeave(Room));

        Assert.Empty(registry.Heard(Guest, Envelope.WriteRelay(Room, Host, [1])));
    }

    [Fact]
    public void A_room_whose_host_went_quiet_is_forgotten()
    {
        var registry = New();
        Publish(registry, Room);

        Advance(RoomRegistry.Forgotten.TotalSeconds + 1);
        registry.Sweep();

        Assert.Equal(0, registry.RoomCount);
        Assert.Single(Of(registry.Heard(Guest, Envelope.WriteJoin(Room)), Envelope.Kind.NoRoom));
    }

    [Fact]
    public void A_room_still_publishing_is_kept()
    {
        var registry = New();
        Publish(registry, Room);

        for (int i = 0; i < 10; i++)
        {
            Advance(RoomRegistry.Forgotten.TotalSeconds / 2);
            Publish(registry, Room);
            registry.Sweep();
        }

        Assert.Equal(1, registry.RoomCount);
    }

    /// <summary>
    /// A player whose machine was closed leaves nothing behind to say so, and
    /// a room that keeps a ghost seat is a room that fills up and stops
    /// letting anybody in.
    /// </summary>
    [Fact]
    public void A_member_that_went_quiet_is_dropped_but_the_room_stays()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        for (int i = 0; i < 4; i++)
        {
            Advance(RoomRegistry.Forgotten.TotalSeconds / 3);
            Publish(registry, Room);
            registry.Sweep();
        }

        Assert.Equal(1, registry.RoomCount);
        Assert.Empty(registry.Heard(Guest, Envelope.WriteRelay(Room, Host, [1])));
    }

    [Fact]
    public void Relaying_is_itself_a_sign_of_life()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        for (int i = 0; i < 4; i++)
        {
            Advance(RoomRegistry.Forgotten.TotalSeconds / 3);
            Publish(registry, Room);
            registry.Heard(Guest, Envelope.WriteRelay(Room, Host, [1]));
            registry.Sweep();
        }

        Assert.Single(Of(registry.Heard(Guest, Envelope.WriteRelay(Room, Host, [1])),
            Envelope.Kind.Relayed));
    }

    [Fact]
    public void A_full_room_refuses_the_next_arrival()
    {
        var registry = New();
        Publish(registry, Room);

        for (int i = 0; i < RoomRegistry.MaxMembers - 1; i++)
        {
            var member = new IPEndPoint(IPAddress.Parse("203.0.113.100"), 40000 + i);
            Assert.Single(Of(registry.Heard(member, Envelope.WriteJoin(Room)),
                Envelope.Kind.Joined));
        }

        var late = new IPEndPoint(IPAddress.Parse("203.0.113.200"), 41000);
        Assert.Single(Of(registry.Heard(late, Envelope.WriteJoin(Room)), Envelope.Kind.NoRoom));
    }

    [Fact]
    public void A_full_server_refuses_a_new_room_and_keeps_the_ones_it_has()
    {
        var registry = New();
        for (int i = 0; i < RoomRegistry.MaxRooms; i++)
        {
            var host = new IPEndPoint(IPAddress.Parse("198.51.100.10"), 40000 + i);
            Publish(registry, Guid.NewGuid(), host: host);
        }

        var replies = registry.Heard(
            new IPEndPoint(IPAddress.Parse("198.51.100.99"), 45000),
            Envelope.WritePublish(Guid.NewGuid(), true, Card(1)));

        Assert.Single(Of(replies, Envelope.Kind.NoRoom));
        Assert.Equal(RoomRegistry.MaxRooms, registry.RoomCount);
    }

    [Fact]
    public void Nonsense_is_ignored_rather_than_answered()
    {
        var registry = New();

        Assert.Empty(registry.Heard(Guest, [1, 2, 3]));
        Assert.Empty(registry.Heard(Guest, []));
        Assert.Empty(registry.Heard(Guest, [Envelope.Magic, 99, 1]));
    }

    /// <summary>
    /// A race is the one time a host never publishes: publishing happens in
    /// the lobby loop, and that loop ends when the race begins. So a room
    /// expired thirty seconds into every race and this stopped forwarding for
    /// it - the cars stopped where they were and never moved again, on both
    /// screens, with every counter still climbing because sending is not
    /// arriving.
    /// </summary>
    [Fact]
    public void A_host_that_only_relays_keeps_its_room()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        // Two minutes of racing and not one publish. Both ends send, which
        // is what a race is.
        for (int i = 0; i < 24; i++)
        {
            Advance(5);
            registry.Heard(Host, Envelope.WriteRelay(Room, Guest, [1]));
            registry.Heard(Guest, Envelope.WriteRelay(Room, Host, [1]));
            registry.Sweep();
        }

        Assert.Equal(1, registry.RoomCount);
        Assert.Single(Of(registry.Heard(Host, Envelope.WriteRelay(Room, Guest, [1])),
            Envelope.Kind.Relayed));
    }

    /// <summary>
    /// And a guest who has gone quiet cannot take the host down with it. The
    /// check on the destination used to come first, so once a departed guest
    /// had been swept out of the room, the host's own traffic stopped counting
    /// as a sign of life and the room died with the host still talking.
    /// </summary>
    [Fact]
    public void A_departed_guest_does_not_stop_the_host_counting_as_present()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        for (int i = 0; i < 24; i++)
        {
            Advance(5);
            registry.Heard(Host, Envelope.WriteRelay(Room, Guest, [1]));
            registry.Sweep();
        }

        Assert.Equal(1, registry.RoomCount);
    }

    /// <summary>
    /// A guest relaying does not, though. A room outliving its host would be a
    /// room clients go on relaying into after there is nobody to receive.
    /// </summary>
    [Fact]
    public void A_guest_relaying_into_a_room_does_not_keep_it_alive()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        for (int i = 0; i < 24; i++)
        {
            Advance(5);
            registry.Heard(Guest, Envelope.WriteRelay(Room, Host, [1]));
            registry.Sweep();
        }

        Assert.Equal(0, registry.RoomCount);
    }

    /// <summary>
    /// A host that reconnects comes from a new port, because its NAT gave it a
    /// new mapping. The room is the same room and its members should still be
    /// able to reach it.
    /// </summary>
    [Fact]
    public void A_host_that_comes_back_on_a_new_port_takes_the_room_with_it()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        var moved = new IPEndPoint(Host.Address, Host.Port + 1);
        Publish(registry, Room, host: moved);

        var replies = registry.Heard(Guest, Envelope.WriteRelay(Room, moved, [1]));
        var relayed = Of(replies, Envelope.Kind.Relayed);
        Assert.Single(relayed);
        Assert.Equal(moved, relayed[0].To);
    }
}
