using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

public class SessionTests
{
    DateTime _now = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
    Session NewSession(string name = "ian") => new(name, () => _now);
    void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    static Room RoomWith(params Player[] players) =>
        new(Guid.NewGuid(), "room", "Trial Mountain", 6, players);

    [Fact]
    public void Starts_out_browsing()
    {
        Assert.Equal(SessionPhase.Browsing, NewSession().Phase);
    }

    [Fact]
    public void Hosting_creates_a_room_containing_the_host()
    {
        var session = NewSession();
        session.Host("Ian's room", "Trial Mountain");

        Assert.Equal(SessionPhase.Hosting, session.Phase);
        Assert.Equal("Ian's room", session.Current!.Name);
        Assert.Single(session.Current.Players);
        Assert.Equal("ian", session.Current.Players[0].Name);
    }

    [Fact]
    public void Joining_adopts_the_room()
    {
        var session = NewSession("guest");
        Assert.True(session.Join(RoomWith(new Player("ian", "", false))));

        Assert.Equal(SessionPhase.Joined, session.Phase);
        Assert.Contains(session.Current!.Players, p => p.Name == "guest");
    }

    [Fact]
    public void Joining_a_full_room_is_refused()
    {
        var full = new Room(Guid.NewGuid(), "room", "track", 2,
            [new Player("a", "", false), new Player("b", "", false)]);

        var session = NewSession("guest");
        Assert.False(session.Join(full));
        Assert.Equal(SessionPhase.Browsing, session.Phase);
    }

    [Fact]
    public void Leaving_returns_to_browsing()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.Leave();

        Assert.Equal(SessionPhase.Browsing, session.Phase);
        Assert.Null(session.Current);
    }

    [Fact]
    public void Start_needs_more_than_one_player()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.SetReady("ian", true);

        Assert.False(session.CanStart);
    }

    [Fact]
    public void Start_needs_everyone_ready()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("ian", "", true), new Player("guest", "", false)));

        Assert.False(session.CanStart);
    }

    [Fact]
    public void Host_can_start_when_all_are_ready()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("ian", "", true), new Player("guest", "", true)));

        Assert.True(session.CanStart);
    }

    [Fact]
    public void A_client_never_gets_to_start()
    {
        var session = NewSession("guest");
        session.Join(RoomWith(new Player("ian", "", true)));
        session.OnRemoteState(RoomWith(
            new Player("ian", "", true), new Player("guest", "", true)));

        Assert.False(session.CanStart);
    }

    [Fact]
    public void Host_drops_a_player_that_goes_silent()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("ian", "", false), new Player("guest", "", false)));
        session.OnHeard("guest");

        Advance(3.5);
        session.Tick();

        Assert.DoesNotContain(session.Current!.Players, p => p.Name == "guest");
    }

    [Fact]
    public void Host_keeps_a_player_that_keeps_talking()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("ian", "", false), new Player("guest", "", false)));

        for (int i = 0; i < 4; i++)
        {
            session.OnHeard("guest");
            Advance(1.0);
            session.Tick();
        }

        Assert.Contains(session.Current!.Players, p => p.Name == "guest");
    }

    [Fact]
    public void Client_disconnects_when_the_host_goes_silent()
    {
        var session = NewSession("guest");
        session.Join(RoomWith(new Player("ian", "", false)));
        session.OnRemoteState(RoomWith(
            new Player("ian", "", false), new Player("guest", "", false)));

        Advance(3.5);
        session.Tick();

        Assert.Equal(SessionPhase.Disconnected, session.Phase);
        Assert.NotNull(session.StatusMessage);
    }

    [Fact]
    public void Setting_ready_shows_up_in_the_room()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.SetReady("ian", true);

        Assert.True(session.Current!.Players[0].Ready);
    }

    [Fact]
    public void A_joiner_readies_itself_and_not_the_host()
    {
        // Join appends, so the local player is not row zero for a client.
        var session = NewSession("guest");
        session.Join(RoomWith(new Player("ian", "", false)));
        session.SetReady(session.PlayerName, true);

        Assert.False(session.Current!.Players.Single(p => p.Name == "ian").Ready);
        Assert.True(session.Current.Players.Single(p => p.Name == "guest").Ready);
    }

    [Fact]
    public void Setting_a_car_shows_up_in_the_room()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.SetCar("ian", "Skyline GT-R");

        Assert.Equal("Skyline GT-R", session.Current!.Players[0].Car);
    }
}
