using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class LanDiscoveryTests
{
    // A port unlikely to collide with anything else on the machine.
    const int Port = 34719;

    DateTime _now = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
    void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    static Room Sample(string name = "Ian's room") =>
        new(Guid.NewGuid(), name, "Trial Mountain", 6, [new Player("ian", "", false)]);

    /// <summary>Gives the datagram time to make it across loopback.</summary>
    static void Settle(LanDiscovery listener)
    {
        for (int i = 0; i < 50 && listener.Rooms.Count == 0; i++)
        {
            listener.Tick();
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Finds_an_announced_room()
    {
        using var listener = new LanDiscovery(Port, () => _now);
        using var host = new LanDiscovery(Port, () => _now);

        var room = Sample();
        host.Announce(room);
        Settle(listener);

        Assert.Contains(listener.Rooms, r => r.Id == room.Id);
    }

    [Fact]
    public void Carries_the_room_details()
    {
        using var listener = new LanDiscovery(Port, () => _now);
        using var host = new LanDiscovery(Port, () => _now);

        host.Announce(Sample("Trial Mountain Cup"));
        Settle(listener);

        var found = Assert.Single(listener.Rooms);
        Assert.Equal("Trial Mountain Cup", found.Name);
        Assert.Equal("Trial Mountain", found.Track);
        Assert.Single(found.Players);
    }

    [Fact]
    public void Ignores_its_own_announcements()
    {
        using var host = new LanDiscovery(Port, () => _now);
        var room = Sample();
        host.LocalRoomId = room.Id;

        host.Announce(room);
        for (int i = 0; i < 20; i++) { host.Tick(); Thread.Sleep(10); }

        Assert.Empty(host.Rooms);
    }

    [Fact]
    public void Forgets_a_room_that_stops_announcing()
    {
        using var listener = new LanDiscovery(Port, () => _now);
        using var host = new LanDiscovery(Port, () => _now);

        host.Announce(Sample());
        Settle(listener);
        Assert.NotEmpty(listener.Rooms);

        Advance(3.5);
        listener.Tick();

        Assert.Empty(listener.Rooms);
    }

    [Fact]
    public void Keeps_a_room_that_keeps_announcing()
    {
        using var listener = new LanDiscovery(Port, () => _now);
        using var host = new LanDiscovery(Port, () => _now);

        var room = Sample();
        host.Announce(room);
        Settle(listener);

        for (int i = 0; i < 3; i++)
        {
            Advance(1.0);
            host.Announce(room);
            for (int j = 0; j < 20; j++) { listener.Tick(); Thread.Sleep(5); }
        }

        Assert.NotEmpty(listener.Rooms);
    }

    [Fact]
    public void Survives_a_garbage_datagram()
    {
        using var listener = new LanDiscovery(Port, () => _now);
        using var sender = new System.Net.Sockets.UdpClient();
        sender.EnableBroadcast = true;

        var junk = new byte[32];
        Random.Shared.NextBytes(junk);
        sender.Send(junk, junk.Length,
            new System.Net.IPEndPoint(System.Net.IPAddress.Broadcast, Port));

        for (int i = 0; i < 20; i++) { listener.Tick(); Thread.Sleep(5); }

        Assert.Empty(listener.Rooms);
    }
}
