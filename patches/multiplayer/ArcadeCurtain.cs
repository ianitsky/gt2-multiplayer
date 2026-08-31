using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;

namespace GT2Port.Multiplayer;

/// <summary>
/// Hides the arcade while a launched race walks it, and says what is loading
/// instead.
///
/// A race started from the lobby still goes through the arcade, because the
/// arcade is what loads the car: its first screen's step method is what ticks
/// the loader through its eight steps, so the screen has to run until the car
/// is in memory. A measured run puts that at 1.9s, and for all of it the
/// player is looking at a menu they did not open and cannot use.
///
/// The screen keeps running. Only the picture goes.
///
/// Not by blanking the emulated display, which is the console's own way to do
/// this and the wrong one here: GP1(03) belongs to the game, the only function
/// that writes it is 0x8007F830, and the display environment is put up again
/// every frame - a curtain held there would be lifted and redrawn all the way
/// through. <see cref="HostWindow.OutputHidden"/> is held by the host, where
/// the game cannot reach it.
/// </summary>
public static class ArcadeCurtain
{
    /// <summary>
    /// Off when GT2_SHOW_ARCADE asks to watch the arcade being walked, which
    /// is the only reason to want those frames on screen.
    /// </summary>
    static readonly bool Wanted =
        Environment.GetEnvironmentVariable("GT2_SHOW_ARCADE") is (null or "");

    /// <summary>
    /// How long a curtain may stay up before it lifts itself.
    ///
    /// DirectRace gives the car 30 seconds and then goes anyway, so this only
    /// ever fires if the path that lifts it is never reached at all - and the
    /// failure it would otherwise leave is a black screen with no way out.
    /// </summary>
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(40);

    static DateTime _raised;
    static readonly Panel Curtain = new();
    static bool _registered;

    /// <summary>Whether the arcade is being hidden right now.</summary>
    public static bool IsUp => Curtain.IsOpen;

    /// <summary>
    /// What is happening behind it, in one line. Set by whoever is doing the
    /// waiting - a curtain with nothing to say is indistinguishable from a
    /// hang.
    /// </summary>
    public static string Doing { get; set; } = "";

    public static void Raise(string doing)
    {
        if (!Wanted) return;

        Doing = doing;
        _raised = DateTime.UtcNow;

        if (!_registered)
        {
            _registered = true;
            PanelManager.Register(Curtain);
        }

        Curtain.IsOpen = true;
        HostWindow.OutputHidden = true;
        Console.Error.WriteLine($"[curtain] up - {doing}");
    }

    public static void Drop(string why)
    {
        if (!Curtain.IsOpen) return;

        Curtain.IsOpen = false;
        HostWindow.OutputHidden = false;
        Console.Error.WriteLine(
            $"[curtain] down after {(DateTime.UtcNow - _raised).TotalSeconds:F1}s - {why}");
    }

    /// <summary>
    /// What the player looks at while the arcade is hidden.
    ///
    /// It is also what lifts a curtain nobody else lifted: this draws every
    /// frame whatever the game is doing, so it is the one place that cannot be
    /// skipped by the game taking a path this did not expect.
    /// </summary>
    sealed class Panel : IFloatingPanel
    {
        public string Name => "Loading";
        public bool IsOpen { get; set; }

        public void Draw()
        {
            if (DateTime.UtcNow - _raised > Patience)
            {
                Drop("nothing lifted it in time");
                return;
            }

            var size = ImGui.GetIO().DisplaySize;
            ImGui.SetNextWindowPos(size * 0.5f, ImGuiCond.Always, new Vector2(0.5f, 0.5f));

            if (ImGui.Begin(Name,
                    ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize
                    | ImGuiWindowFlags.NoSavedSettings))
            {
                ImGui.Text("Loading the race");
                ImGui.Separator();
                ImGui.TextDisabled(Doing.Length > 0 ? Doing : "...");
                ImGui.TextDisabled($"{(DateTime.UtcNow - _raised).TotalSeconds:F1}s");
            }

            ImGui.End();
        }
    }
}
