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

    // ---- Finding 1: a reconnecting player is kicked instantly ----

    [Fact]
    public void A_reconnecting_player_survives_the_next_tick()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("ian", "", false), new Player("guest", "", false)));
        session.OnHeard("guest");

        Advance(3.5);
        session.Tick();
        Assert.DoesNotContain(session.Current!.Players, p => p.Name == "guest");

        // guest reconnects under the same name, with no time elapsed since.
        session.OnRemoteState(RoomWith(
            new Player("ian", "", false), new Player("guest", "", false)));
        session.Tick();

        Assert.Contains(session.Current!.Players, p => p.Name == "guest");
    }

    [Fact]
    public void OnRemoteState_refreshes_liveness_for_players_it_keeps_seeing()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("ian", "", false), new Player("guest", "", false)));

        // guest is reported by every remote state, but never calls OnHeard directly.
        for (int i = 0; i < 4; i++)
        {
            Advance(1.0);
            session.OnRemoteState(RoomWith(
                new Player("ian", "", false), new Player("guest", "", false)));
            session.Tick();
        }

        Assert.Contains(session.Current!.Players, p => p.Name == "guest");
    }

    // ---- Finding 2: duplicate names corrupt the room ----

    [Fact]
    public void Joining_a_room_that_already_has_your_name_is_refused()
    {
        var session = NewSession("ian");
        Assert.False(session.Join(RoomWith(new Player("ian", "", false))));
        Assert.Equal(SessionPhase.Browsing, session.Phase);
    }

    [Fact]
    public void Remote_state_with_duplicate_names_keeps_only_the_first()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("ian", "first", false),
            new Player("guest", "", false),
            new Player("ian", "second", true)));

        var ians = session.Current!.Players.Where(p => p.Name == "ian").ToList();
        Assert.Single(ians);
        Assert.Equal("first", ians[0].Car);
    }

    [Fact]
    public void SetReady_toggles_exactly_one_row()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("ian", "first", false),
            new Player("guest", "", false),
            new Player("ian", "second", false)));

        session.SetReady("ian", true);

        Assert.True(session.Current!.Players.Single(p => p.Name == "ian").Ready);
        Assert.False(session.Current.Players.Single(p => p.Name == "guest").Ready);
    }

    // ---- Finding 3: recovery from Disconnected ----

    [Fact]
    public void Leaving_a_disconnected_session_returns_to_browsing()
    {
        var session = NewSession("guest");
        session.Join(RoomWith(new Player("ian", "", false)));
        session.OnRemoteState(RoomWith(new Player("ian", "", false)));

        Advance(3.5);
        session.Tick();
        Assert.Equal(SessionPhase.Disconnected, session.Phase);

        session.Leave();

        Assert.Equal(SessionPhase.Browsing, session.Phase);
        Assert.Null(session.Current);
    }

    [Fact]
    public void Hosting_from_a_disconnected_session_starts_a_room()
    {
        var session = NewSession("guest");
        session.Join(RoomWith(new Player("ian", "", false)));
        session.OnRemoteState(RoomWith(new Player("ian", "", false)));

        Advance(3.5);
        session.Tick();
        Assert.Equal(SessionPhase.Disconnected, session.Phase);

        session.Host("guest's room", "track");

        Assert.Equal(SessionPhase.Hosting, session.Phase);
        Assert.Equal("guest's room", session.Current!.Name);
    }

    // ---- Finding 4: StatusMessage clearing is untested ----

    [Fact]
    public void Leaving_clears_the_status_message_left_by_a_host_timeout()
    {
        var session = NewSession("guest");
        session.Join(RoomWith(new Player("ian", "", false)));
        session.OnRemoteState(RoomWith(new Player("ian", "", false)));

        Advance(3.5);
        session.Tick();
        Assert.NotNull(session.StatusMessage);

        session.Leave();

        Assert.Null(session.StatusMessage);
    }

    // ---- Finding 5: timeout boundary ----

    [Fact]
    public void Host_keeps_a_player_at_exactly_the_timeout()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("ian", "", false), new Player("guest", "", false)));
        session.OnHeard("guest");

        Advance(Session.Timeout.TotalSeconds);
        session.Tick();

        Assert.Contains(session.Current!.Players, p => p.Name == "guest");
    }

    [Fact]
    public void Host_drops_a_player_just_past_the_timeout()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("ian", "", false), new Player("guest", "", false)));
        session.OnHeard("guest");

        Advance(Session.Timeout.TotalSeconds + 0.001);
        session.Tick();

        Assert.DoesNotContain(session.Current!.Players, p => p.Name == "guest");
    }

    [Fact]
    public void Client_stays_connected_at_exactly_the_host_timeout()
    {
        var session = NewSession("guest");
        session.Join(RoomWith(new Player("ian", "", false)));
        session.OnRemoteState(RoomWith(new Player("ian", "", false)));

        Advance(Session.Timeout.TotalSeconds);
        session.Tick();

        Assert.Equal(SessionPhase.Joined, session.Phase);
    }

    [Fact]
    public void Client_disconnects_just_past_the_host_timeout()
    {
        var session = NewSession("guest");
        session.Join(RoomWith(new Player("ian", "", false)));
        session.OnRemoteState(RoomWith(new Player("ian", "", false)));

        Advance(Session.Timeout.TotalSeconds + 0.001);
        session.Tick();

        Assert.Equal(SessionPhase.Disconnected, session.Phase);
    }

    // ---- Finding 6: Host() should use the RoomState constant, not a literal ----

    [Fact]
    public void Hosting_uses_the_global_max_players_constant()
    {
        var session = NewSession();
        session.Host("room", "track");

        Assert.Equal(RoomState.MaxPlayers, session.Current!.MaxPlayers);
    }

    // ---- Finding 8: Join clamps an untrusted room.MaxPlayers ----

    [Fact]
    public void Join_clamps_max_players_to_the_global_cap()
    {
        var oversized = new Room(Guid.NewGuid(), "room", "track", 999,
            [new Player("ian", "", false)]);

        var session = NewSession("guest");
        Assert.True(session.Join(oversized));

        Assert.Equal(RoomState.MaxPlayers, session.Current!.MaxPlayers);
    }
}
