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

    // A getter rather than a snapshot reference: ModeHook now builds and
    // drops the LanSession per role (host binds one way, client another),
    // so the instance this panel should use for sending changes underneath
    // it, and can be null (e.g. while Browsing, between rooms).
    readonly Func<LanSession?> _lanSession;
    readonly CourseMaps _courseMaps;

    string _playerName;
    string _roomName = "";
    string _track = CourseTable.All[0].Code;
    int _maxPlayers = RoomState.MaxPlayers;
    string _car = "";
    Guid? _carSeededFor;
    bool _creating;

    public MultiplayerPanel(Session session, LanDiscovery discovery, Func<LanSession?> lanSession, CourseMaps courseMaps)
    {
        _session = session;
        _discovery = discovery;
        _lanSession = lanSession;
        _courseMaps = courseMaps;
        _playerName = session.PlayerName;
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

    /// <summary>
    /// Leaves the current room and discards any pending start request for it.
    ///
    /// A start request for a room nobody is in anymore is meaningless, so the
    /// two always go together. Shared by the "Leave" button and by
    /// ModeHook.RunLobby's own exit, so a lobby visit that ends any other way
    /// (closing the panel) leaves the session just as clean as pressing Leave
    /// does.
    ///
    /// A joined client announces its departure before Session.Leave clears
    /// Current - once it is cleared there is no room id left to address the
    /// message to. The host doesn't need this: it isn't leaving a room it
    /// owns, it's tearing one down, and there is nobody to notify.
    /// </summary>
    public void LeaveRoom()
    {
        if (_session.Phase == SessionPhase.Joined && _session.Current is { } room &&
            _discovery.TryGetHostAddress(room.Id, out var hostAddress))
        {
            _lanSession()?.SendLeave(_session, hostAddress);
        }

        _session.Leave();
        StartRequested = false;
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
                if (_creating) DrawCreate(); else DrawRoomList();
                break;
            case SessionPhase.Disconnected:
                // Disconnected is a message on the room list, not a screen
                // of its own. The message itself was already captured above
                // (StatusMessage), so it still renders this frame; Leave()
                // then returns the phase to Browsing before the room list is
                // drawn, so renaming works immediately instead of being
                // refused until Join/Host/Leave is pressed, and the message
                // does not linger past this one frame.
                _session.Leave();
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
        if (ImGui.InputText("Your name", ref _playerName, 32))
            _session.Rename(_playerName);
        ImGui.Separator();

        // Only the most recent outcome, never a history of occasional loss -
        // that is exactly what LastSendFailure already tracks. Whichever
        // channel most recently failed to send is the one worth naming;
        // both share the same likely cause, a firewall blocking this app,
        // since binding either socket is what would have prompted for it.
        if (_discovery.LastSendFailure is { } discoveryFailure)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f),
                $"Broadcasting failed: {discoveryFailure.Message} - a firewall may be blocking this app.");
        else if (_lanSession()?.LastSendFailure is { } sessionFailure)
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f),
                $"Sending failed: {sessionFailure.Message} - a firewall may be blocking this app.");

        ImGui.Text("Rooms on this network");
        ImGui.Separator();

        var rooms = _discovery.Rooms;
        if (rooms.Count == 0)
            ImGui.TextDisabled("Looking for rooms...");

        foreach (var room in rooms)
        {
            ImGui.PushID(room.Id.ToString());
            var host = room.Players.Count > 0 ? room.Players[0].Name : "";
            ImGui.Text($"{room.Name}   {host}   {room.Players.Count}/{room.MaxPlayers}   {CourseTable.DisplayName(room.Track)}");
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
        DrawCourseGrid();
        ImGui.SliderInt("Player limit", ref _maxPlayers, 2, RoomState.MaxPlayers);

        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_roomName));
        if (ImGui.Button("Create"))
        {
            _session.Host(_roomName, _track, _maxPlayers);
            _discovery.LocalRoomId = _session.Current!.Id;
            _creating = false;
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel")) _creating = false;
    }

    // 96x96 to match the map pictures themselves, plus enough of a margin for
    // a wrapped display name beneath - the longest names ("Red Rock Valley
    // Speedway") run to three lines at that width.
    const float MapSize = 96f;
    static readonly Vector2 CourseCellSize = new(MapSize + 16f, MapSize + 16f + 3f * 16f);

    /// <summary>
    /// One cell per course, Tarmac then Dirt, wrapped to the window's width
    /// and scrollable so 27 of them cannot push the Create/Cancel buttons off
    /// the screen. Every cell is drawn at the same fixed size regardless of
    /// whether its map loaded, so a missing picture never reflows the grid.
    /// </summary>
    void DrawCourseGrid()
    {
        var spacing = ImGui.GetStyle().ItemSpacing;

        ImGui.BeginChild("CourseGrid", new Vector2(0f, 260f), ImGuiChildFlags.Border);

        float columnsF = (ImGui.GetContentRegionAvail().X + spacing.X) / (CourseCellSize.X + spacing.X);
        int columns = Math.Max(1, (int)columnsF);

        CourseSurface? surface = null;
        int column = 0;
        for (int i = 0; i < CourseTable.All.Count; i++)
        {
            var course = CourseTable.All[i];
            if (course.Surface != surface)
            {
                surface = course.Surface;
                column = 0;
                if (i > 0) ImGui.Spacing();
                ImGui.TextUnformatted(surface == CourseSurface.Tarmac ? "Tarmac" : "Dirt");
            }

            if (column > 0) ImGui.SameLine();
            DrawCourseCell(course);

            column++;
            if (column >= columns) column = 0;
        }

        ImGui.EndChild();
    }

    void DrawCourseCell(Course course)
    {
        ImGui.PushID(course.Code);

        bool selected = _track == course.Code;
        var borderColor = selected ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text] : new Vector4(0f, 0f, 0f, 0f);
        ImGui.PushStyleColor(ImGuiCol.Border, borderColor);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4f, 4f));

        ImGui.BeginChild("cell", CourseCellSize, ImGuiChildFlags.Border);

        // Line art on transparency: tinted by the current text colour so it
        // stays visible on a dark theme instead of vanishing.
        uint texture = _courseMaps.TextureFor(course.Code);
        if (texture != 0)
        {
            var tint = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
            ImGui.Image((nint)texture, new Vector2(MapSize, MapSize), Vector2.Zero, Vector2.One, tint);
        }
        else
        {
            ImGui.Dummy(new Vector2(MapSize, MapSize));
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPos().X + MapSize);
        ImGui.TextUnformatted(course.Name);
        ImGui.PopTextWrapPos();

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();

        if (ImGui.IsItemClicked())
            _track = course.Code;

        ImGui.PopID();
    }

    void DrawLobby()
    {
        var room = _session.Current!;

        // _car is a scratch buffer for the InputText widget below, not the
        // source of truth - the local player's row is. Reseed it whenever a
        // different room is entered (a fresh Host/Join, room.Id having
        // changed) so a room's leftover text doesn't sit in the field for
        // the next one, and so the field starts matching whatever car this
        // player already had in the room (e.g. rejoining).
        if (_carSeededFor != room.Id)
        {
            _carSeededFor = room.Id;
            _car = room.Players.FirstOrDefault(p => p.Name == _session.PlayerName)?.Car ?? "";
        }

        ImGui.Text($"{room.Name}   {CourseTable.DisplayName(room.Track)}");
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
        if (ImGui.Button("Leave")) LeaveRoom();
    }
}
