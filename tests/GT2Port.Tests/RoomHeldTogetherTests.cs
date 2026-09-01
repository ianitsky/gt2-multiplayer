using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// A room has to survive the race it exists to start. Nobody sends lobby
/// traffic while the game is running, and three seconds of quiet is all it
/// takes for a room to decide everybody has left - which is what happened: the
/// client lost the room to "The host left the room", and whoever finished first
/// came back to a room list and had to join by hand.
/// </summary>
public class RoomHeldTogetherTests
{
    DateTime _now = new(2026, 8, 31, 23, 0, 0, DateTimeKind.Utc);

    Session Hosting()
    {
        var session = new Session("ian", () => _now);
        session.Host("ian's room", "seattle_short", "special");
        session.ApplyClientIntent("les", "buc9n", ready: true);
        return session;
    }

    Session Joined()
    {
        var session = new Session("les", () => _now);
        Assert.True(session.Join(new Room(
            Guid.NewGuid(), "ian's room", "seattle_short", "special", 6,
            [new Player("ian", "buc9n", true)])));
        return session;
    }

    [Fact]
    public void A_client_loses_the_room_when_the_host_really_does_go_quiet()
    {
        var session = Joined();

        _now += Session.Timeout + TimeSpan.FromSeconds(1);
        session.Tick();

        Assert.Null(session.Current);
        Assert.Equal(SessionPhase.Disconnected, session.Phase);
    }

    /// <summary>
    /// And keeps it through a race however long, because a race has no length
    /// worth guessing at - ninety-nine laps is allowed.
    /// </summary>
    [Fact]
    public void A_client_keeps_the_room_through_a_race()
    {
        var session = Joined();
        session.HoldTheRoomTogether();

        _now += TimeSpan.FromHours(1);
        session.Tick();

        Assert.NotNull(session.Current);
        Assert.Equal(SessionPhase.Joined, session.Phase);
    }

    [Fact]
    public void A_host_keeps_its_players_through_a_race()
    {
        var session = Hosting();
        session.HoldTheRoomTogether();

        _now += TimeSpan.FromHours(1);
        session.Tick();

        Assert.Equal(2, session.Current!.Players.Count);
    }

    /// <summary>
    /// Letting go has to forgive the silence it was holding through, or the
    /// very next tick drops the whole room for having been quiet all race.
    /// </summary>
    [Fact]
    public void Letting_go_does_not_drop_everyone_for_the_silence_it_forgave()
    {
        var session = Hosting();
        session.HoldTheRoomTogether();

        _now += TimeSpan.FromHours(1);
        session.LetTheRoomBreatheAgain();
        session.Tick();

        Assert.Equal(2, session.Current!.Players.Count);
        Assert.False(session.RoomIsHeldTogether);
    }

    [Fact]
    public void And_the_room_drops_people_again_afterwards()
    {
        var session = Hosting();
        session.HoldTheRoomTogether();
        session.LetTheRoomBreatheAgain();

        _now += Session.Timeout + TimeSpan.FromSeconds(1);
        session.Tick();

        Assert.Equal(["ian"], session.Current!.Players.Select(p => p.Name));
    }
}
