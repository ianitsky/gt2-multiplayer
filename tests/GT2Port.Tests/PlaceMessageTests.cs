using System.Net;
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// A place message carries a pose - three coordinates and the three angles the
/// game rebuilds a car's rotation from - and it is the only thing that moves
/// between machines during a race. If a field were lost or reordered on the
/// way, a remote car would be drawn somewhere it is not or facing a way it is
/// not.
/// </summary>
public class PlaceMessageTests
{
    static (LanSession Host, LanSession Client) Pair()
    {
        var host = LanSession.ForHost(0, () => DateTime.UtcNow);
        var client = LanSession.ForClient(host.BoundPort, () => DateTime.UtcNow);
        return (host, client);
    }

    [Fact]
    public void CarriesAWholePoseFromOneMachineToTheOther()
    {
        var (host, client) = Pair();
        using (host)
        using (client)
        {
            // Negative on every field: a course runs either side of its origin
            // and a heading past half a turn reads negative, so a field widened
            // or read unsigned would show up here and nowhere else.
            var sent = new RemoteCars.Pose(
                new RemoteCars.Place(-1_400_000, 987_654, -3), -2048, 17, -1000);

            client.SendPlace(3, sent, IPAddress.Loopback);
            Thread.Sleep(60);
            host.CollectPlaces();

            Assert.True(host.Places.TryGetValue(3, out var got));
            Assert.Equal(sent, got);
        }
    }

    /// <summary>
    /// A place is a snapshot, so only the newest matters. Keeping an older one
    /// would draw a car where it used to be.
    /// </summary>
    [Fact]
    public void KeepsOnlyTheNewestFromASeat()
    {
        var (host, client) = Pair();
        using (host)
        using (client)
        {
            var older = new RemoteCars.Pose(new RemoteCars.Place(1, 1, 1), 1, 1, 1);
            var newer = new RemoteCars.Pose(new RemoteCars.Place(2, 2, 2), 2, 2, 2);

            client.SendPlace(1, older, IPAddress.Loopback);
            client.SendPlace(1, newer, IPAddress.Loopback);
            Thread.Sleep(60);
            host.CollectPlaces();

            Assert.Equal(newer, host.Places[1]);
        }
    }
}
