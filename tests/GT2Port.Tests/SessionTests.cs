using System.Reflection;
using GT2Port.Multiplayer;
using RecompOne.Runtime.Host.Window;
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
    public void OnRemoteState_preserves_local_player_state_when_duplicates_exist()
    {
        var session = NewSession("ian");
        session.Host("room", "track");
        session.SetReady("ian", true);            // local player is ready

        session.OnRemoteState(RoomWith(
            new Player("ian", "", false),          // stale duplicate, listed first
            new Player("guest", "", false),
            new Player("ian", "", true)));         // the real entry, listed second

        // the local player's Ready must remain true (local knowledge preserved)
        Assert.True(session.Current!.Players.Single(p => p.Name == "ian").Ready);
    }

    [Fact]
    public void Joining_a_room_that_already_has_your_name_is_refused()
    {
        var session = NewSession("ian");
        Assert.False(session.Join(RoomWith(new Player("ian", "", false))));
        Assert.Equal(SessionPhase.Browsing, session.Phase);
    }

    [Fact]
    public void Remote_state_with_duplicate_names_preserves_local_player_car()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.SetCar("ian", "local_car");
        session.OnRemoteState(RoomWith(
            new Player("ian", "first", false),
            new Player("guest", "", false),
            new Player("ian", "second", true)));

        var ians = session.Current!.Players.Where(p => p.Name == "ian").ToList();
        Assert.Single(ians);
        Assert.Equal("local_car", ians[0].Car);
    }

    [Fact]
    public void Remote_state_with_duplicate_names_keeps_first_for_other_players()
    {
        var session = NewSession("host");
        session.Host("room", "track");
        session.OnRemoteState(RoomWith(
            new Player("host", "", false),
            new Player("guest", "first_car", false),
            new Player("guest", "second_car", true)));

        var guests = session.Current!.Players.Where(p => p.Name == "guest").ToList();
        Assert.Single(guests);
        Assert.Equal("first_car", guests[0].Car);
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

    [Fact]
    public void Join_allows_entry_to_room_with_duplicates_but_fewer_distinct_players_than_cap()
    {
        var roomWithDupes = new Room(Guid.NewGuid(), "room", "track", 3,
            [new Player("ian", "", false), new Player("guest", "", false), new Player("ian", "", false)]);

        var session = NewSession("alice");
        Assert.True(session.Join(roomWithDupes));
        Assert.Equal(SessionPhase.Joined, session.Phase);
    }

    // ---- Task 7: host-side application of a client's intent ----

    [Fact]
    public void ApplyClientIntent_adds_an_unknown_name()
    {
        var session = NewSession();
        session.Host("room", "track");

        session.ApplyClientIntent("guest", "Supra", true);

        var guest = session.Current!.Players.SingleOrDefault(p => p.Name == "guest");
        Assert.NotNull(guest);
        Assert.Equal("Supra", guest!.Car);
        Assert.True(guest.Ready);
    }

    [Fact]
    public void ApplyClientIntent_updates_a_known_name_rather_than_duplicating_it()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.ApplyClientIntent("guest", "Supra", false);

        session.ApplyClientIntent("guest", "Skyline", true);

        var guests = session.Current!.Players.Where(p => p.Name == "guest").ToList();
        Assert.Single(guests);
        Assert.Equal("Skyline", guests[0].Car);
        Assert.True(guests[0].Ready);
    }

    [Fact]
    public void ApplyClientIntent_on_a_full_room_adds_nothing()
    {
        var session = NewSession();
        session.Host("room", "track");
        for (int i = 0; i < RoomState.MaxPlayers - 1; i++)
            session.ApplyClientIntent($"guest{i}", "", false);
        Assert.Equal(RoomState.MaxPlayers, session.Current!.Players.Count);

        session.ApplyClientIntent("one-too-many", "", false);

        Assert.Equal(RoomState.MaxPlayers, session.Current!.Players.Count);
        Assert.DoesNotContain(session.Current.Players, p => p.Name == "one-too-many");
    }

    [Fact]
    public void ApplyClientIntent_cannot_alter_the_hosts_own_row()
    {
        var session = NewSession(); // host is "ian"
        session.Host("room", "track");

        session.ApplyClientIntent("ian", "Skyline", true);

        var host = Assert.Single(session.Current!.Players);
        Assert.Equal("ian", host.Name);
        Assert.Equal("", host.Car);
        Assert.False(host.Ready);
    }

    [Fact]
    public void ApplyClientIntent_keeps_the_player_alive_across_a_tick_past_the_timeout()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.ApplyClientIntent("guest", "", false);

        Advance(2.0);
        session.ApplyClientIntent("guest", "", false); // refreshes the keep-alive
        Advance(2.0); // 4s total, past the 3s timeout, but only 2s since the refresh
        session.Tick();

        Assert.Contains(session.Current!.Players, p => p.Name == "guest");
    }

    [Fact]
    public void ApplyClientLeave_removes_the_player_and_allows_an_immediate_rejoin()
    {
        var session = NewSession();
        session.Host("room", "track");
        session.ApplyClientIntent("guest", "", false);
        Assert.Contains(session.Current!.Players, p => p.Name == "guest");

        session.ApplyClientLeave("guest");
        Assert.DoesNotContain(session.Current!.Players, p => p.Name == "guest");

        // Rejoining immediately must not be instantly re-kicked by a stale
        // keep-alive timestamp left over from before the leave.
        session.ApplyClientIntent("guest", "", false);
        session.Tick();

        Assert.Contains(session.Current!.Players, p => p.Name == "guest");
    }

    [Fact]
    public void ApplyClientLeave_cannot_remove_the_hosts_own_row()
    {
        var session = NewSession(); // host is "ian"
        session.Host("room", "track");

        session.ApplyClientLeave("ian");

        Assert.Contains(session.Current!.Players, p => p.Name == "ian");
    }

    [Fact]
    public void ApplyClientIntent_and_ApplyClientLeave_do_nothing_while_joined()
    {
        var session = NewSession("guest");
        session.Join(RoomWith(new Player("ian", "", false)));

        session.ApplyClientIntent("someone", "", true);
        session.ApplyClientLeave("ian");

        Assert.Equal(2, session.Current!.Players.Count);
        Assert.Contains(session.Current.Players, p => p.Name == "ian");
        Assert.DoesNotContain(session.Current.Players, p => p.Name == "someone");
    }

    [Fact]
    public void ApplyClientIntent_and_ApplyClientLeave_do_nothing_while_browsing()
    {
        var session = NewSession();

        session.ApplyClientIntent("someone", "", true);
        session.ApplyClientLeave("someone");

        Assert.Equal(SessionPhase.Browsing, session.Phase);
        Assert.Null(session.Current);
    }

    // ---- Task 5 review round 2: overlay-load gate, panel close, stale state ----
    //
    // ModeHook.RunLobby and MultiplayerPanel.Draw both need a live ImGui
    // context (Draw calls ImGui.Begin/InputText/Button directly, and RunLobby
    // blocks pumping the host window) so neither is reachable from a plain
    // xunit test. What's below covers everything that is: the entry-point
    // gate itself, and the state MultiplayerPanel carries across a lobby
    // visit, which is exactly what Findings 2 and 3 of the round-2 review
    // were about.

    [Fact]
    public void ModeHook_declines_an_entry_point_that_is_not_simulation()
    {
        var panelsBefore = PanelManager.Panels.Count;

        bool entered = ModeHook.TryEnterLobby(0x12345678u);

        Assert.False(entered);

        // No panel registered and no session built - confirmed via reflection
        // since ModeHook exposes neither as a public member (same pattern
        // LanDiscoveryTests uses to reach LanDiscovery's private _socket).
        Assert.Equal(panelsBefore, PanelManager.Panels.Count);
        var sessionField = typeof(ModeHook).GetField("_session", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.Null(sessionField!.GetValue(null));
    }

    [Fact]
    public void Panel_start_request_is_consumed_exactly_once()
    {
        // Port is well clear of LanDiscoveryTests' BasePort+0..9 range and
        // ModeHook's own DiscoveryPort - this discovery instance never sends
        // or receives, it only satisfies MultiplayerPanel's constructor.
        using var discovery = new LanDiscovery(34740, () => _now);
        using var lanSession = new LanSession(34750, () => _now);
        var panel = new MultiplayerPanel(NewSession(), discovery, lanSession);

        Assert.False(panel.TryConsumeStartRequest());

        typeof(MultiplayerPanel).GetProperty(nameof(MultiplayerPanel.StartRequested))!
            .SetValue(panel, true);

        Assert.True(panel.TryConsumeStartRequest());
        Assert.False(panel.TryConsumeStartRequest());
    }

    [Fact]
    public void Leaving_the_room_clears_a_pending_start_request()
    {
        using var discovery = new LanDiscovery(34741, () => _now);
        using var lanSession = new LanSession(34751, () => _now);
        var session = NewSession();
        session.Host("room", "track");
        var panel = new MultiplayerPanel(session, discovery, lanSession);

        typeof(MultiplayerPanel).GetProperty(nameof(MultiplayerPanel.StartRequested))!
            .SetValue(panel, true);

        panel.LeaveRoom();

        Assert.False(panel.TryConsumeStartRequest());
        Assert.Equal(SessionPhase.Browsing, session.Phase);
        Assert.Null(session.Current);
    }
}
