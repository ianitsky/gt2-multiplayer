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
    readonly CarCatalogue _carCatalogue;

    string _playerName;
    string _roomName = "";
    string _track = CourseTable.All[0].Code;
    string _carGroup;
    int _maxPlayers = RoomState.MaxPlayers;
    bool _creating;

    // Set when the Create screen is (re)entered, so the grid scrolls the
    // selected course into view once, the same frame it becomes visible,
    // instead of opening scrolled to the top with the selection off screen.
    bool _scrollToSelection;

    // One frame of hover lag: whether the border/background should highlight
    // is decided before BeginChild runs for that cell, so it reflects last
    // frame's IsItemHovered() result rather than this one's - imperceptible
    // at any real frame rate.
    readonly Dictionary<string, bool> _cellHovered = new(StringComparer.Ordinal);

    public MultiplayerPanel(Session session, LanDiscovery discovery, Func<LanSession?> lanSession, CourseMaps courseMaps, CarCatalogue carCatalogue)
    {
        _session = session;
        _discovery = discovery;
        _lanSession = lanSession;
        _courseMaps = courseMaps;
        _carCatalogue = carCatalogue;
        _playerName = session.PlayerName;
        _carGroup = carCatalogue.Groups.Count > 0 ? carCatalogue.Groups[0].Id : "";
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
        if (ImGui.Button("Create a room"))
        {
            _creating = true;
            _scrollToSelection = true;
        }
    }

    void DrawCreate()
    {
        ImGui.Text("New room");
        ImGui.Separator();
        ImGui.InputText("Name", ref _roomName, 32);
        ImGui.TextUnformatted("Course");
        DrawCourseGrid();
        ImGui.TextUnformatted("Car class");
        DrawCarGroupSelector();
        ImGui.SliderInt("Player limit", ref _maxPlayers, 2, RoomState.MaxPlayers);

        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_roomName));
        if (ImGui.Button("Create"))
        {
            _session.Host(_roomName, _track, _carGroup, _maxPlayers);
            _discovery.LocalRoomId = _session.Current!.Id;
            _creating = false;
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel")) _creating = false;
    }

    /// <summary>
    /// One radio button per group, in catalogue order, on a single row - a
    /// handful of groups at most (the arcade five plus whatever a config
    /// file adds), so a grid like the course picker's would be overkill.
    /// The radio dot itself is the "chosen one marked" the brief asks for.
    /// </summary>
    void DrawCarGroupSelector()
    {
        var groups = _carCatalogue.Groups;
        for (int i = 0; i < groups.Count; i++)
        {
            if (i > 0) ImGui.SameLine();
            if (ImGui.RadioButton(groups[i].Name, _carGroup == groups[i].Id))
                _carGroup = groups[i].Id;
        }
    }

    // 96x96 logical pixels to match the map pictures themselves at 100% DPI
    // and UI scale - scaled at draw time by the same factor the rest of the
    // panel already follows (HostWindow.DpiScale * the UI scale setting, the
    // same product Theme.Scale applies to ScaleAllSizes), rather than being
    // pinned to a fixed pixel size that only looks right at 100%.
    const float MapSize = 96f;

    /// <summary>
    /// The map picture's current on-screen size and the cell that frames it -
    /// derived from the host's live scale and font metrics rather than fixed
    /// in pixels (review IMPORTANT 3). Enough margin beside the map for a
    /// wrapped display name below it: three lines at the current line height,
    /// which is what the longest names ("Red Rock Valley Speedway") need.
    /// </summary>
    (float MapSize, Vector2 CellSize) CourseCellMetrics()
    {
        float scale = RecompOne.Runtime.Host.HostWindow.DpiScale * ImGui.GetIO().FontGlobalScale;
        float mapSize = MapSize * scale;
        var margin = ImGui.GetStyle().ItemSpacing;
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var cellSize = new Vector2(mapSize + margin.X, mapSize + margin.Y + 3f * lineHeight);
        return (mapSize, cellSize);
    }

    /// <summary>
    /// One cell per course, Tarmac then Dirt, wrapped to the window's width
    /// and scrollable so 27 of them cannot push the Create/Cancel buttons off
    /// the screen. Every cell is drawn at the same fixed size regardless of
    /// whether its map loaded, so a missing picture never reflows the grid.
    /// </summary>
    void DrawCourseGrid()
    {
        var (mapSize, cellSize) = CourseCellMetrics();
        var spacing = ImGui.GetStyle().ItemSpacing;
        float lineHeight = ImGui.GetTextLineHeightWithSpacing();

        // Tall enough for the section label plus one full row of cells, with
        // enough of a second row peeking in to hint that it scrolls - a
        // multiple of the (now live-scaled) cell height rather than a
        // constant that only matched one particular font size.
        float gridHeight = lineHeight + spacing.Y + cellSize.Y * 1.5f;
        ImGui.BeginChild("CourseGrid", new Vector2(0f, gridHeight), ImGuiChildFlags.Border);

        float columnsF = (ImGui.GetContentRegionAvail().X + spacing.X) / (cellSize.X + spacing.X);
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
            DrawCourseCell(course, mapSize, cellSize);

            if (_scrollToSelection && course.Code == _track)
            {
                ImGui.SetScrollHereY(0.5f);
                _scrollToSelection = false;
            }

            column++;
            if (column >= columns) column = 0;
        }

        ImGui.EndChild();
    }

    void DrawCourseCell(Course course, float mapSize, Vector2 cellSize)
    {
        ImGui.PushID(course.Code);

        bool selected = _track == course.Code;
        bool hovered = _cellHovered.TryGetValue(course.Code, out var wasHovered) && wasHovered;

        // A cell nobody has selected still gets a faint border so the grid
        // reads as a set of clickable cells rather than bare pictures with
        // one outlined outlier, and brightens on hover for feedback that the
        // cell under the mouse is about to be picked.
        var textColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        var borderColor = selected ? textColor
            : new Vector4(textColor.X, textColor.Y, textColor.Z, hovered ? 0.6f : 0.25f);
        ImGui.PushStyleColor(ImGuiCol.Border, borderColor);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4f, 4f));

        ImGui.BeginChild("cell", cellSize, ImGuiChildFlags.Border);

        // Line art on transparency: tinted by the current text colour so it
        // stays visible on a dark theme instead of vanishing.
        uint texture = _courseMaps.TextureFor(course.Code);
        if (texture != 0)
        {
            ImGui.Image((nint)texture, new Vector2(mapSize, mapSize), Vector2.Zero, Vector2.One, textColor);
        }
        else
        {
            ImGui.Dummy(new Vector2(mapSize, mapSize));
        }

        ImGui.PushTextWrapPos(ImGui.GetCursorPos().X + mapSize);
        ImGui.TextUnformatted(course.Name);
        ImGui.PopTextWrapPos();

        ImGui.EndChild();
        _cellHovered[course.Code] = ImGui.IsItemHovered();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();

        if (ImGui.IsItemClicked())
            _track = course.Code;

        ImGui.PopID();
    }

    void DrawLobby()
    {
        var room = _session.Current!;

        ImGui.Text($"{room.Name}   {CourseTable.DisplayName(room.Track)}");
        ImGui.Separator();

        foreach (var player in room.Players)
            ImGui.Text($"{(player.Ready ? "[ready]" : "[    ]")}  {player.Name}  {_carCatalogue.DisplayName(player.Car)}");

        ImGui.Separator();
        DrawCarList(room);

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

    /// <summary>
    /// The car picker for the room's own group. The group comes from the
    /// room, not from whatever this player last had selected while hosting
    /// - a client's room.CarGroup may not even be one this build knows about
    /// (an id from a config file it doesn't have), in which case this shows
    /// the raw id and offers no cars rather than throwing or guessing at a
    /// substitute group.
    /// </summary>
    void DrawCarList(Room room)
    {
        ImGui.TextUnformatted("Car");

        if (!_carCatalogue.TryFind(room.CarGroup, out var group))
        {
            ImGui.TextColored(new Vector4(1f, 0.6f, 0.2f, 1f),
                $"This build does not have car group \"{room.CarGroup}\".");
            return;
        }

        string currentCar = room.Players.FirstOrDefault(p => p.Name == _session.PlayerName)?.Car ?? "";

        // Eight rows tall, scrolling for the rest - sized from the live line
        // height and frame padding rather than a pixel constant, the same
        // lesson the course grid had to relearn at 150% display scale.
        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        float listHeight = rowHeight * 8f + ImGui.GetStyle().FramePadding.Y * 2f;
        ImGui.BeginChild("CarList", new Vector2(0f, listHeight), ImGuiChildFlags.Border);
        foreach (var code in group.Cars)
        {
            if (ImGui.Selectable(_carCatalogue.DisplayName(code), code == currentCar))
                _session.SetCar(_session.PlayerName, code);
        }
        ImGui.EndChild();
    }
}
