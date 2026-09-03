using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Config;
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

    /// <summary>
    /// How long the room's race will be, as the create screen has it: a number
    /// of laps, or a number of minutes when <see cref="_byTheClock"/>.
    /// </summary>
    int _laps = RaceLaps.AsBuilt;
    int _minutes = TimedRace.Shortest;
    bool _byTheClock;

    /// <summary>Whether the room being made runs a qualifying session first.</summary>
    bool _qualifying;

    /// <summary>What the room list's "join by address" rows hold.</summary>
    string _address = "";
    string _addressSecret = "";

    /// <summary>And what the create screen's secret field holds.</summary>
    string _secret = "";
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

    public MultiplayerPanel(Session session, LanDiscovery discovery, Func<LanSession?> lanSession, Func<RelaySession?> relay, CourseMaps courseMaps, CarCatalogue carCatalogue)
    {
        _session = session;
        _discovery = discovery;
        _lanSession = lanSession;
        _relay = relay;
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
    /// <summary>
    /// Joins a room the relay knows about. The relay is told first, because
    /// until it has admitted this machine to the room it will not carry a
    /// single datagram for it - the session that follows would be knocking
    /// into a server that drops what it sends.
    /// </summary>
    void JoinOverTheRelay(Guid roomId, Room room)
    {
        _relay()?.Join(roomId);
        _session.Join(room);
    }

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
        // 640x420 at the default 16px font this panel was designed against -
        // expressed as a multiple of the live font size instead of that pixel
        // constant, so the window's own size grows with the font the same
        // way Theme.ScaleAllSizes already grows everything drawn inside it
        // (review Important 1). FirstUseEver only: a player who has resized
        // the window keeps whatever size they chose.
        float fontSize = ImGui.GetFontSize();
        ImGui.SetNextWindowSize(new Vector2(fontSize * 40f, fontSize * 26f), ImGuiCond.FirstUseEver);
        bool open = IsOpen;
        if (!ImGui.Begin("Multiplayer", ref open))
        {
            IsOpen = open;
            ImGui.End();
            return;
        }

        if (_session.StatusMessage is { } message)
            DrawWarning(message);

        switch (_session.Phase)
        {
            case SessionPhase.Browsing:
            // Knocking is the room list with a line saying so, not a screen of
            // its own - and it has to be *a* screen, because the only way out
            // of a knock at an address nobody is listening at is the Stop
            // button the room list draws. Without this case the panel drew
            // nothing at all and the client had no way back.
            case SessionPhase.Knocking:
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

    /// <summary>
    /// A warning-coloured line of text that may carry text off the wire or
    /// off the disc - a room name, a player name, a group id. Neither
    /// <c>ImGui.Text</c> nor <c>ImGui.TextColored</c> is safe for that: both
    /// bind to a printf-style native function, so a stray '%' in untrusted
    /// text would be read as a conversion specifier. Route through
    /// <c>TextUnformatted</c> instead, which takes the string as data, and
    /// apply the colour with a style push around it - the same thing the
    /// runtime's own (internal, so unreachable from here) ImGuiEx helper
    /// does for exactly this reason.
    /// </summary>
    static void DrawWarning(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.6f, 0.2f, 1f));
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// Cuts <paramref name="text"/> down to whatever fits in
    /// <paramref name="maxWidth"/>, ellipsis included, measured with the live
    /// font rather than assumed - the same "measured, not assumed to fit"
    /// rule <see cref="DrawCarGroupSelector"/> already follows (review Minor
    /// 5). Returns the text unchanged when it already fits or
    /// <paramref name="maxWidth"/> is non-positive is handled by simply
    /// returning the empty ellipsis-only cut, never throwing or going negative.
    /// </summary>
    static string Truncate(string text, float maxWidth)
    {
        if (maxWidth <= 0f) return "";
        if (ImGui.CalcTextSize(text).X <= maxWidth) return text;

        const string ellipsis = "...";
        float ellipsisWidth = ImGui.CalcTextSize(ellipsis).X;

        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (ImGui.CalcTextSize(text[..mid]).X + ellipsisWidth <= maxWidth) lo = mid;
            else hi = mid - 1;
        }

        return lo == 0 ? ellipsis : text[..lo] + ellipsis;
    }

    /// <summary>What a player is typing into the code box.</summary>
    string _roomCodeTyped = "";

    /// <summary>
    /// What a player is typing into the server box, started from whatever is
    /// already configured so that changing it is editing rather than
    /// retyping.
    /// </summary>
    string _relayTyped = RelaySettings.Address;

    /// <summary>
    /// The relay, if one is configured. A function rather than an instance:
    /// it is built and dropped as the address setting changes, and the panel
    /// outlives any one of them.
    /// </summary>
    readonly Func<RelaySession?> _relay;

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
            DrawWarning($"Broadcasting failed: {discoveryFailure.Message} - a firewall may be blocking this app.");
        else if (_lanSession()?.LastSendFailure is { } sessionFailure)
            DrawWarning($"Sending failed: {sessionFailure.Message} - a firewall may be blocking this app.");

        ImGui.Text("Rooms on this network");
        ImGui.Separator();

        var rooms = _discovery.Rooms;
        if (rooms.Count == 0)
            ImGui.TextDisabled("Looking for rooms...");

        foreach (var room in rooms)
        {
            ImGui.PushID(room.Id.ToString());
            var host = room.Players.Count > 0 ? room.Players[0].Name : "";
            var carClass = _carCatalogue.TryFind(room.CarGroup, out var carGroup) ? carGroup.Name : room.CarGroup;
            var label = $"{room.Name}   {host}   {room.Players.Count}/{room.MaxPlayers}   {CourseTable.DisplayName(room.Track)}   {carClass}   {HowLong(room)}";

            bool full = room.Players.Count >= room.MaxPlayers;
            string buttonText = full ? "Full" : "Join";

            // Neither the group's name (only its id has a length check) nor a
            // room/host name at 150% display scale has a bound on this row's
            // width, and the window has no horizontal scrollbar - truncate
            // to what is actually left after the button so Join/Full always
            // has its place, the same "control you cannot reach" shape
            // Important 6 fixed for the group selector above it (Minor 5).
            float buttonWidth = ImGui.CalcTextSize(buttonText).X + ImGui.GetStyle().FramePadding.X * 2f;
            float available = ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X;
            ImGui.TextUnformatted(Truncate(label, available));
            ImGui.SameLine();

            ImGui.BeginDisabled(full);
            if (ImGui.Button(buttonText)) _session.Join(room);
            ImGui.EndDisabled();
            ImGui.PopID();
        }

        // Rooms that were never on this network. Same row, same button - where
        // a room was found is not something a player should have to think
        // about, only whether they can get into it.
        ImGui.Separator();
        ImGui.TextUnformatted("Rooms on the internet");

        DrawRelayAddress();

        if (_relay() is { } relay)
        {
            var adverts = relay.Rooms;
            if (adverts.Count == 0) ImGui.TextDisabled("None right now");

            foreach (var advert in adverts)
            {
                // A card that will not deserialise is a room published by a
                // newer build than this one. Skipping it is right: the panel
                // cannot draw a room it cannot read, and refusing to draw the
                // rest because of it would be worse.
                if (!RoomState.TryDeserialise(advert.Card, out var internetRoom)) continue;

                ImGui.PushID(advert.Id.ToString());
                var itsHost = internetRoom.Players.Count > 0 ? internetRoom.Players[0].Name : "";
                var itsClass = _carCatalogue.TryFind(internetRoom.CarGroup, out var itsGroup)
                    ? itsGroup.Name
                    : internetRoom.CarGroup;
                var itsLabel =
                    $"{internetRoom.Name}   {itsHost}"
                    + $"   {internetRoom.Players.Count}/{internetRoom.MaxPlayers}"
                    + $"   {CourseTable.DisplayName(internetRoom.Track)}   {itsClass}"
                    + $"   {HowLong(internetRoom)}   code {advert.Code}";

                bool itIsFull = internetRoom.Players.Count >= internetRoom.MaxPlayers;
                string itsButton = itIsFull ? "Full" : "Join";

                // Same truncation the local rows use, and for the same reason:
                // a room name at 150% display scale has no bound on this row's
                // width and the window has no horizontal scrollbar.
                float itsButtonWidth = ImGui.CalcTextSize(itsButton).X
                    + ImGui.GetStyle().FramePadding.X * 2f;
                float itsRoom = ImGui.GetContentRegionAvail().X - itsButtonWidth
                    - ImGui.GetStyle().ItemSpacing.X;
                ImGui.TextUnformatted(Truncate(itsLabel, itsRoom));
                ImGui.SameLine();

                ImGui.BeginDisabled(itIsFull);
                if (ImGui.Button(itsButton)) JoinOverTheRelay(advert.Id, internetRoom);
                ImGui.EndDisabled();
                ImGui.PopID();
            }
        }

        // A room nobody announced can only be reached by being told where it
        // is - which is every room that is not on this network.
        ImGui.Separator();
        ImGui.TextUnformatted("Or join by address");

        ImGui.InputText("Address", ref _address, 64);
        ImGui.InputText("Secret", ref _addressSecret, 64);

        ImGui.BeginDisabled(_address.Trim().Length == 0);
        if (ImGui.Button("Join by address")) _session.Knock(_address, _addressSecret);
        ImGui.EndDisabled();

        if (_session.Phase == SessionPhase.Knocking)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop")) _session.Leave();
            DrawKnocking();
        }

        if (_relay() is { } byCode)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Or join by code");

            ImGui.InputText("Code", ref _roomCodeTyped, 16);
            ImGui.SameLine();
            if (ImGui.Button("Join by code")) byCode.JoinByCode(_roomCodeTyped);

            if (byCode.Refused)
                DrawWarning("No room with that code - it may have closed, or been mistyped.");
        }

        ImGui.Separator();
        if (ImGui.Button("Create a room"))
        {
            _creating = true;
            _scrollToSelection = true;
        }
    }

    /// <summary>
    /// How long to knock unanswered before saying what is probably wrong.
    ///
    /// A knock across a working network is answered in one round trip. Ten
    /// seconds is long past that and short of the patience of somebody staring
    /// at a screen that has told them nothing.
    /// </summary>
    static readonly TimeSpan LongEnoughToWorry = TimeSpan.FromSeconds(10);

    /// <summary>
    /// What is happening to a knock, in as much detail as the socket has.
    ///
    /// UDP reports no failure, so an unanswered knock looks the same whether
    /// the address is wrong, the port is closed, the host is not running, or
    /// the datagrams are arriving and being refused. What separates those is
    /// not one fact but the shape of several: knocks going out, datagrams
    /// coming back, the address they were actually sent to, and whether the
    /// socket itself refused to send. All four are on the screen, because the
    /// person reading it is the one who can check the router.
    /// </summary>
    void DrawKnocking()
    {
        var wire = _lanSession();
        var waited = _session.KnockingFor;

        string where = Session.TryReadAddress(_session.KnockingAt, out var resolved)
            ? resolved.ToString()
            : _session.KnockingAt;

        // Names resolve to addresses, and the address is what a router rule and
        // a packet capture are written against.
        string typedHost = _session.KnockingAt.Split(':')[0];
        string alsoKnownAs = where.StartsWith(typedHost + ":") ? "" : $" ({_session.KnockingAt})";

        ImGui.TextDisabled($"Knocking at {where}{alsoKnownAs} - {waited.TotalSeconds:F0}s");

        if (wire is null)
        {
            DrawWarning("No session socket on this machine - nothing has been sent.");
            return;
        }

        ImGui.TextDisabled(
            $"{wire.KnocksSent} knock(s) sent from port {wire.BoundPort},"
            + $" {wire.DatagramsHeard} datagram(s) heard back");

        if (wire.LastSendFailure is { } failure)
        {
            DrawWarning($"Sending failed: {failure.Message}");
            return;
        }

        if (wire.DatagramsHeard > 0)
        {
            // Something answered, so the round trip works and the silence is
            // about content rather than reachability - a wrong secret is the
            // one the host drops without a word.
            ImGui.TextDisabled($"Last heard from {wire.LastHeardFrom}");
            if (waited > LongEnoughToWorry)
                DrawWarning("Answered, but not let in - the room's secret may be wrong,"
                    + " or the room may be full.");
            return;
        }

        if (waited <= LongEnoughToWorry) return;

        DrawWarning($"Nothing has come back in {waited.TotalSeconds:F0}s.");
        ImGui.TextDisabled("The host must forward UDP to the port above, on its own machine's");
        ImGui.TextDisabled("address, and its firewall must allow this app. Check that the address");
        ImGui.TextDisabled("is the host's public one, not the one it sees itself as.");
    }

    /// <summary>
    /// Where the rendezvous server is, as a box to paste into.
    ///
    /// Without this the address could only be set by an environment variable
    /// or by hand-editing interface.ini, which is the same kind of demand as
    /// asking somebody to forward a port - and this whole feature exists to
    /// stop making demands like that.
    /// </summary>
    void DrawRelayAddress()
    {
        if (RelaySettings.ForcedByEnvironment)
        {
            ImGui.TextDisabled($"Server: {RelaySettings.Address} (set by GT2_RELAY)");
            return;
        }

        ImGui.InputText("Server", ref _relayTyped, 64);
        ImGui.SameLine();

        bool changed = _relayTyped.Trim() != RelaySettings.Address.Trim();
        ImGui.BeginDisabled(!changed);
        if (ImGui.Button("Use"))
        {
            RelaySettings.Address = _relayTyped.Trim();

            // Written out now rather than at shutdown: a player who pastes an
            // address and then has the game crash should not have to paste it
            // again.
            try { ConfigManager.SaveView(PanelManager.Panels); }
            catch (IOException) { /* the value still holds for this run */ }
        }
        ImGui.EndDisabled();

        if (_relayTyped.Trim().Length == 0)
            ImGui.TextDisabled("Paste the address of a relay to see rooms outside this network");
        else if (!RelaySettings.TryReadAddress(_relayTyped, out _))
            DrawWarning($"\"{_relayTyped.Trim()}\" is not an address this can reach.");
        else if (_relay() is null)
            ImGui.TextDisabled("Not connected yet");
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

        DrawHowLongTheRaceWillBe();

        // Off by default: a room that says nothing about qualifying runs the
        // race straight away, which is what every room did before this existed.
        ImGui.Checkbox($"Qualifying first ({Qualifying.Laps} laps)", ref _qualifying);

        ImGui.InputText("Secret", ref _secret, 64);
        ImGui.TextDisabled("Empty lets anybody in. A room reachable from the internet wants one.");

        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(_roomName));
        if (ImGui.Button("Create"))
        {
            _session.Host(_roomName, _track, _carGroup, _maxPlayers,
                          _laps, _byTheClock ? _minutes : TimedRace.ByLaps, _qualifying, _secret);
            _discovery.LocalRoomId = _session.Current!.Id;
            _creating = false;
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel")) _creating = false;
    }

    /// <summary>
    /// One radio button per group, in catalogue order, wrapped to the
    /// window's width the way <see cref="DrawCourseGrid"/> wraps its cells -
    /// measured, not assumed to fit. Adding groups is the entire point of
    /// this feature, so a fixed-width window at a display scale above 100%,
    /// or one custom group added at 150%, must not clip the trailing ones
    /// past the right edge with no way to reach them (review Important 6).
    /// The radio dot itself is the "chosen one marked" the brief asks for.
    /// </summary>
    void DrawCarGroupSelector()
    {
        var groups = _carCatalogue.Groups;
        float windowVisibleX = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        float spacing = ImGui.GetStyle().ItemSpacing.X;

        for (int i = 0; i < groups.Count; i++)
        {
            ImGui.PushID(groups[i].Id);
            if (ImGui.RadioButton(groups[i].Name, _carGroup == groups[i].Id))
                _carGroup = groups[i].Id;
            ImGui.PopID();

            if (i + 1 < groups.Count)
            {
                float thisEndX = ImGui.GetItemRectMax().X;
                float nextEndX = thisEndX + spacing + RadioButtonWidth(groups[i + 1].Name);
                if (nextEndX < windowVisibleX) ImGui.SameLine();
            }
        }
    }

    static float RadioButtonWidth(string label) =>
        ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(label).X;

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

        // What this lobby is for, said plainly: the same room is a qualifying
        // lobby and then a race lobby, and the difference decides what the
        // Start button is about to do.
        ImGui.TextUnformatted(Qualifying.Title(room));
        ImGui.TextDisabled(
            $"{room.Name}   {CourseTable.DisplayName(room.Track)}"
            + $"   {(room.QualifyingNext ? $"{Qualifying.Laps} laps" : HowLong(room))}");

        // The host is the only machine that can be joined at an address, and
        // the only one that cannot look its own up.
        if (_session.Phase == SessionPhase.Hosting)
        {
            // What to tell people, in the order of how much is known. A mapped
            // port means a real address to read out; anything else means "your
            // address", which is what this said before the router was ever
            // asked - and a refusal is worth naming, because the player is the
            // one who can act on it.
            switch (PortMapping.State)
            {
                case PortMapping.How.Mapped when PortMapping.Outside is { } outside:
                    ImGui.TextDisabled($"Others join at {outside}");
                    break;
                case PortMapping.How.Mapped:
                    ImGui.TextDisabled(
                        $"Your router forwarded port {ModeHook.SessionPortNumber} - "
                        + "others join at your public address");
                    break;
                case PortMapping.How.Trying:
                    ImGui.TextDisabled("Asking your router to open the port...");
                    break;
                case PortMapping.How.Refused:
                    ImGui.TextDisabled(
                        $"Others join at your address, port {ModeHook.SessionPortNumber}");
                    DrawWarning($"The router would not open it: {PortMapping.Why}");
                    break;
                default:
                    ImGui.TextDisabled(
                        $"Others join at your address, port {ModeHook.SessionPortNumber}");
                    break;
            }

            // The other half of a knock nobody answered. A router that is not
            // forwarding and a firewall that is dropping are invisible to the
            // client - both are simply silence - and they are equally invisible
            // to a host that is only told about the players who got through.
            // This is told about the ones who did not: a count that stays at
            // zero while somebody is knocking says the datagrams are not
            // reaching this machine at all, which is a different problem in a
            // different place from anything the client can see.
            // And the code, for a room somebody has to be told about rather
            // than find.
            if (_relay() is { Code.Length: > 0 } mine)
                ImGui.TextDisabled($"Or by code {mine.Code} through the relay");

            if (_lanSession() is { } wire)
            {
                ImGui.TextDisabled($"{wire.DatagramsHeard} datagram(s) heard on it"
                    + (wire.LastHeardFrom is { } last ? $", last from {last}" : ""));

                if (wire.LastSendFailure is { } failure)
                    DrawWarning($"Sending failed: {failure.Message}");
            }
        }

        DrawTheLastRace();

        ImGui.Separator();

        var grid = Seats.Drivers(room.Players);
        for (int place = 0; place < grid.Count; place++)
        {
            var player = grid[place];

            // The grid is the room's own order, so the number beside a name is
            // simply where they sit in it - and moving them is moving the room.
            if (_session.Phase == SessionPhase.Hosting) MoveButtons(player, place, grid.Count);

            ImGui.TextUnformatted($"{place + 1}.  {(player.Ready ? "[ready]" : "[    ]")}  {player.Name}  {_carCatalogue.DisplayName(player.Car)}");

            // Beside the name, because the point of choosing a paint in the
            // lobby is that everyone can see who is in what before the race.
            var paints = _carCatalogue.Colours(player.Car);
            if (player.Colour >= paints.Count) continue;
            ImGui.SameLine();
            Swatch(paints[player.Colour], selected: false, hovered: false);
        }

        foreach (var viewer in Seats.Viewers(room.Players))
            ImGui.TextUnformatted($"{(viewer.Ready ? "[ready]" : "[    ]")}  {viewer.Name}  watching");

        ImGui.Separator();

        // What Ready/Not ready, the separator below the list, Start race/
        // Leave, and the "Waiting for..." hint line need, reserved whether
        // or not the hint actually shows this frame - so the list's height
        // does not jump between frames depending on _session.CanStart, and
        // the row below it is never a guess (review Important 1).
        float frameRow = ImGui.GetFrameHeightWithSpacing();
        float hintRow = ImGui.GetTextLineHeightWithSpacing();
        float separatorHeight = ImGui.GetStyle().ItemSpacing.Y * 2f + 1f;

        // The swatches sit under the car list and are measured into what it
        // must leave behind, the same way every other row below it is - a row
        // that reserves nothing is a row that pushes the buttons off the
        // window at 150% scale.
        float colourRow = ImGui.GetTextLineHeightWithSpacing() + SwatchSize + ImGui.GetStyle().ItemSpacing.Y;
        float reservedBelowList = colourRow + frameRow + separatorHeight + frameRow + hintRow;

        if (Watching(room))
        {
            DrawDriverToFollow(room, reservedBelowList);
        }
        else
        {
            DrawCarList(room, reservedBelowList);
            DrawColours(room);
        }

        if (ImGui.Button(Watching(room) ? "Race instead" : "Watch instead"))
            _session.SetWatching(_session.PlayerName, !Watching(room));
        ImGui.SameLine();

        if (ImGui.Button("Ready")) _session.SetReady(_session.PlayerName, true);
        ImGui.SameLine();
        if (ImGui.Button("Not ready")) _session.SetReady(_session.PlayerName, false);

        ImGui.Separator();
        ImGui.BeginDisabled(!_session.CanStart);
        if (ImGui.Button(Qualifying.StartSays(room))) StartRequested = true;
        ImGui.EndDisabled();

        if (_session.Phase == SessionPhase.Hosting && !_session.CanStart)
            ImGui.TextDisabled("Waiting for every player to be ready.");

        ImGui.SameLine();
        if (ImGui.Button("Leave")) LeaveRoom();
    }

    /// <summary>
    /// How long the room's race will be, chosen while the room is being made.
    ///
    /// Here rather than in the lobby: it is a property of the room, like the
    /// track and the class, and the lobby is where competitors sort themselves
    /// out and choose cars. A control that changes what everybody is about to
    /// race does not belong among those.
    ///
    /// Laps and minutes are one control rather than two, because they are one
    /// decision - a race is run to one or the other and never to both, and
    /// showing both sliders would invite somebody to set the one being ignored.
    /// </summary>
    void DrawHowLongTheRaceWillBe()
    {
        if (ImGui.RadioButton("Laps", !_byTheClock)) _byTheClock = false;
        ImGui.SameLine();
        if (ImGui.RadioButton("Time", _byTheClock)) _byTheClock = true;

        if (_byTheClock)
            ImGui.SliderInt("How long", ref _minutes, TimedRace.Shortest, TimedRace.Longest, "%d min");
        else
            ImGui.SliderInt("How many", ref _laps, RaceLaps.Fewest, RaceLaps.Most);
    }

    /// <summary>What the room is racing, for everyone in it to read.</summary>
    static string HowLong(Room room) =>
        room.ByTheClock
            ? $"{room.Minutes} min"
            : $"{room.Laps} lap{(room.Laps == 1 ? "" : "s")}";

    /// <summary>
    /// The two buttons that move a driver up and down the grid, drawn only for
    /// the host - the only machine whose order anybody else reads.
    ///
    /// Disabled rather than hidden at the ends of the grid, so the row keeps
    /// the same shape whoever is in it and the list does not jump as drivers
    /// move through it.
    /// </summary>
    void MoveButtons(Player player, int place, int howMany)
    {
        ImGui.PushID(player.Name);

        ImGui.BeginDisabled(place == 0);
        if (ImGui.SmallButton("^")) _session.MoveOnTheGrid(player.Name, -1);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.BeginDisabled(place >= howMany - 1);
        if (ImGui.SmallButton("v")) _session.MoveOnTheGrid(player.Name, +1);
        ImGui.EndDisabled();

        ImGui.SameLine();
        ImGui.PopID();
    }

    /// <summary>
    /// The race just run, above the room that is about to run another.
    ///
    /// Shown here rather than on a screen of its own because the lobby is where
    /// a race ends now - the return from a race reopens this panel - and a
    /// results screen the player had to dismiss would be a door in the way of
    /// the thing they came back for.
    /// </summary>
    void DrawTheLastRace()
    {
        var standings = RaceStandings.OfTheLastRace;
        if (standings.Count == 0) return;

        bool qualifying = RaceStandings.WereQualifying;

        ImGui.Separator();
        ImGui.TextDisabled(Qualifying.ResultsAre(qualifying));

        foreach (var driver in standings)
        {
            // Everything a session produced, whichever kind it was: a race is
            // decided on laps and total time and a qualifying session on the
            // best lap, but the driver wants to see all three either way.
            string laps = driver.Laps == 1 ? "1 lap" : $"{driver.Laps} laps";

            ImGui.TextUnformatted(
                $"{driver.Place}.  {driver.Name}   {_carCatalogue.DisplayName(driver.Car)}"
                + $"   {laps}   {driver.Clock}   best {driver.BestLap}"
                + (driver.Reported ? "" : "   (no report)"));
        }
    }

    // However little room is left, the list keeps at least this many rows
    // rather than collapsing to a sliver - the floor half of review
    // Important 1.
    const int MinVisibleCarRows = 3;

    /// <summary>Whether this machine's player is in the room to watch.</summary>
    bool Watching(Room room) =>
        room.Players.FirstOrDefault(p => p.Name == _session.PlayerName)?.Watching == true;

    /// <summary>
    /// The drivers a viewer may follow, in place of the car list they have no
    /// use for.
    ///
    /// Which one is followed never leaves this machine: it decides which driver
    /// this viewer's race is built around, and nobody else's race changes
    /// because of it.
    /// </summary>
    void DrawDriverToFollow(Room room, float reservedBelow)
    {
        ImGui.TextUnformatted("Follow");

        var drivers = Seats.Drivers(room.Players);
        if (drivers.Count == 0)
        {
            DrawWarning("Nobody is racing yet - a room of viewers has no race to watch.");
            return;
        }

        string following = _session.WatchedDriver()?.Name ?? "";

        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        float floorHeight = rowHeight * MinVisibleCarRows + ImGui.GetStyle().FramePadding.Y * 2f;
        float listHeight = Math.Max(ImGui.GetContentRegionAvail().Y - reservedBelow, floorHeight);
        ImGui.BeginChild("Drivers", new Vector2(0f, listHeight), ImGuiChildFlags.Border);
        foreach (var driver in drivers)
        {
            ImGui.PushID(driver.Name);
            if (ImGui.Selectable($"{driver.Name}  {_carCatalogue.DisplayName(driver.Car)}",
                                 driver.Name == following))
                _session.Watch(driver.Name);
            ImGui.PopID();
        }
        ImGui.EndChild();
    }

    /// <summary>How big a paint square is, in the UI's own units.</summary>
    static float SwatchSize => ImGui.GetTextLineHeight();

    /// <summary>
    /// The paints the chosen car comes in, as the game orders them.
    ///
    /// No list, no dropdown and no names: the disc gives a swatch per paint
    /// and nothing that says what to call it, so the squares are the whole of
    /// what can honestly be shown. A car with one paint still draws it, since
    /// a row that disappears for some cars is a row that moves the buttons.
    /// </summary>
    void DrawColours(Room room)
    {
        ImGui.TextUnformatted("Colour");

        var mine = room.Players.FirstOrDefault(p => p.Name == _session.PlayerName);
        var paints = _carCatalogue.Colours(mine?.Car ?? "");
        if (paints.Count == 0)
        {
            ImGui.TextDisabled(mine is null or { Car: "" }
                ? "Choose a car first."
                : "This build has no paints for that car.");
            return;
        }

        for (int i = 0; i < paints.Count; i++)
        {
            if (i > 0) ImGui.SameLine();
            ImGui.PushID(i);
            if (Swatch(paints[i], selected: i == mine!.Colour, hovered: false, clickable: true))
                _session.SetColour(_session.PlayerName, (byte)i);
            ImGui.PopID();
        }
    }

    /// <summary>
    /// Draws one paint square, and says whether it was clicked.
    ///
    /// The five bits a channel arrives in are spread over the whole of 0-1
    /// rather than divided by 256: the swatch is a colour in the console's own
    /// depth, so its brightest is the brightest there is, not an eighth of it.
    /// </summary>
    static bool Swatch(CarInfo.Colour paint, bool selected, bool hovered, bool clickable = false)
    {
        var fill = new Vector4(paint.Red / 31f, paint.Green / 31f, paint.Blue / 31f, 1f);
        var edge = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];

        ImGui.PushStyleColor(ImGuiCol.Button, fill);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, fill);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, fill);
        ImGui.PushStyleColor(ImGuiCol.Border,
            selected ? edge : new Vector4(edge.X, edge.Y, edge.Z, 0.25f));
        ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, selected ? 2f : 1f);

        float size = SwatchSize;
        bool clicked = ImGui.Button("", new Vector2(size, size)) && clickable;

        ImGui.PopStyleVar();
        ImGui.PopStyleColor(4);
        return clicked;
    }

    /// <summary>
    /// The car picker for the room's own group. The group comes from the
    /// room, not from whatever this player last had selected while hosting
    /// - a client's room.CarGroup may not even be one this build knows about
    /// (an id from a config file it doesn't have), in which case this shows
    /// the raw id and offers no cars rather than throwing or guessing at a
    /// substitute group.
    /// </summary>
    /// <param name="reservedBelow">
    /// Live-measured height the rows drawn after this list need - Ready/Not
    /// ready, Start race/Leave, and the hint line between them. The list
    /// takes what is left after that, not a row count sized to any one
    /// group: a fixed <c>Math.Min(group.Cars.Count, 8)</c> cap was a no-op
    /// for every shipped group (Special/A/B/C/Rally all hold eight cars or
    /// more), so it reserved the same height whether or not it "capped"
    /// anything, and the buttons below it fell off the window at 150%
    /// display scale with six players in the room (review Important 1).
    /// Floored at <see cref="MinVisibleCarRows"/> so it never collapses to
    /// nothing; the list scrolls past that floor instead.
    /// </param>
    void DrawCarList(Room room, float reservedBelow)
    {
        ImGui.TextUnformatted("Car");

        if (!_carCatalogue.TryFind(room.CarGroup, out var group))
        {
            DrawWarning($"This build does not have car group \"{room.CarGroup}\".");
            return;
        }

        string currentCar = room.Players.FirstOrDefault(p => p.Name == _session.PlayerName)?.Car ?? "";

        float rowHeight = ImGui.GetTextLineHeightWithSpacing();
        float floorHeight = rowHeight * MinVisibleCarRows + ImGui.GetStyle().FramePadding.Y * 2f;
        float listHeight = Math.Max(ImGui.GetContentRegionAvail().Y - reservedBelow, floorHeight);
        ImGui.BeginChild("CarList", new Vector2(0f, listHeight), ImGuiChildFlags.Border);
        foreach (var code in group.Cars)
        {
            ImGui.PushID(code);
            if (ImGui.Selectable(_carCatalogue.DisplayName(code), code == currentCar))
                _session.SetCar(_session.PlayerName, code);
            ImGui.PopID();
        }
        ImGui.EndChild();
    }
}
