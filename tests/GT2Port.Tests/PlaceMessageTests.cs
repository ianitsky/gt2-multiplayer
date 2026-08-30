using System.Net;
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// A place message carries a whole transform - three coordinates and a 3x3 -
/// and it is the only thing that moves between machines during a race. If a
/// word were lost or reordered on the way, a remote car would be drawn
/// somewhere it is not or facing a way it is not.
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
    public void CarriesAWholeTransformFromOneMachineToTheOther()
    {
        var (host, client) = Pair();
        using (host)
        using (client)
        {
            var sent = new uint[9];
            for (int i = 0; i < sent.Length; i++) sent[i] = unchecked((uint)(-1_400_000 + i * 7919));

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
            var older = new uint[9];
            var newer = new uint[9];
            for (int i = 0; i < 9; i++) { older[i] = 1u; newer[i] = 2u; }

            client.SendPlace(1, older, IPAddress.Loopback);
            client.SendPlace(1, newer, IPAddress.Loopback);
            Thread.Sleep(60);
            host.CollectPlaces();

            Assert.Equal(newer, host.Places[1]);
        }
    }
}
