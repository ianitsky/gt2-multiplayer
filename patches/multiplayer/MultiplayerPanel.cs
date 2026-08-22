using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace GT2Port.Multiplayer;

/// <summary>
/// The lobby screens: room list, room creation, and the room itself.
///
/// Which one is showing follows Session.Phase rather than any state of its
/// own, so the screen cannot disagree with the session behind it.
/// </summary>
public sealed class MultiplayerPanel : IPanel
{
    readonly Session _session;
    readonly LanDiscovery _discovery;

    string _roomName = "";
    string _track = "Trial Mountain";
    string _car = "";
    bool _creating;

    public MultiplayerPanel(Session session, LanDiscovery discovery)
    {
        _session = session;
        _discovery = discovery;
    }

    public string Name => "Multiplayer";
    public bool IsOpen { get; set; } = true;
    public bool StartRequested { get; private set; }

    /// <summary>
    /// Reads and clears the start request together, so a caller can only ever
    /// observe it as true once.
    ///
    /// The panel outlives any single lobby visit - PanelManager holds it for
    /// the life of the process - so a plain sticky bool would still read true
    /// the next time this panel is reused, starting a race nobody asked for.
    /// Consuming it here instead of leaving callers to poll the property
    /// closes that gap.
    /// </summary>
    public bool TryConsumeStartRequest()
    {
        if (!StartRequested) return false;
        StartRequested = false;
        return true;
    }

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(640, 420), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin("Multiplayer", ref open))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }

        if (_session.StatusMessage is { } message)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f), message);

        switch (_session.Phase)
        {
            case SessionPhase.Browsing:
            case SessionPhase.Disconnected:
                if (_creating) DrawCreate(); else DrawRoomList();
                break;
            case SessionPhase.Hosting:
            case SessionPhase.Joined:
                DrawLobby();
                break;
        }

        IsOpen = open;
        ImGui.End();
    }

    void DrawRoomList()
    {
        ImGui.Text("Rooms on this network");
        ImGui.Separator();

        var rooms = _discovery.Rooms;
        if (rooms.Count == 0)
            ImGui.TextDisabled("Looking for rooms...");

        foreach (var room in rooms)
        {
            ImGui.PushID(room.Id.ToString());
            ImGui.Text($"{room.Name}   {room.Track}   {room.Players.Count}/{room.MaxPlayers}");
            ImGui.SameLine();

            bool full = room.Players.Count >= room.MaxPlayers;
            ImGui.BeginDisabled(full);
            if (ImGui.Button(full ? "Full" : "Join")) _session.Join(room);
            ImGui.EndDisabled();
            ImGui.PopID();
        }

        ImGui.Separator();
        if (ImGui.Button("Create a room")) _creating = true;
    }

    void DrawCreate()
    {
        ImGui.Text("New room");
        ImGui.Separator();
        ImGui.InputText("Name", ref _roomName, 32);
        ImGui.InputText("Track", ref _track, 32);

        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_roomName));
        if (ImGui.Button("Create"))
        {
            _session.Host(_roomName, _track);
            _discovery.LocalRoomId = _session.Current!.Id;
            _creating = false;
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel")) _creating = false;
    }

    void DrawLobby()
    {
        var room = _session.Current!;
        ImGui.Text($"{room.Name}   {room.Track}");
        ImGui.Separator();

        foreach (var player in room.Players)
            ImGui.Text($"{(player.Ready ? "[ready]" : "[    ]")}  {player.Name}  {player.Car}");

        ImGui.Separator();
        if (ImGui.InputText("My car", ref _car, 32))
            _session.SetCar(_session.PlayerName, _car);

        if (ImGui.Button("Ready")) _session.SetReady(_session.PlayerName, true);
        ImGui.SameLine();
        if (ImGui.Button("Not ready")) _session.SetReady(_session.PlayerName, false);

        ImGui.Separator();
        ImGui.BeginDisabled(!_session.CanStart);
        if (ImGui.Button("Start race")) StartRequested = true;
        ImGui.EndDisabled();

        if (_session.Phase == SessionPhase.Hosting && !_session.CanStart)
            ImGui.TextDisabled("Waiting for every player to be ready.");

        ImGui.SameLine();
        if (ImGui.Button("Leave"))
        {
            _session.Leave();
            // A start requested for this room is meaningless once the room is
            // gone - clear it so a stale request can't start a race for a
            // room nobody is in anymore.
            StartRequested = false;
        }
    }
}
