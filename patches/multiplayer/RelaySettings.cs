using System.Net;
using RecompOne.Runtime.Config;

namespace GT2Port.Multiplayer;

/// <summary>
/// Where the rendezvous server is, remembered between runs.
///
/// Kept in the port's own interface settings rather than in a new file,
/// because <see cref="ViewConfig.GetString"/> and
/// <see cref="ViewConfig.SetString"/> are already there and already persisted -
/// a second settings file would be a second thing to find, back up and get
/// out of step.
///
/// Empty means no relay, and no relay means the game behaves exactly as it did
/// before this existed: local rooms only.
/// </summary>
public static class RelaySettings
{
    /// <summary>
    /// The port a relay listens on unless told otherwise. Most people will
    /// paste an address with no port at all, because a port is not something
    /// most people think about.
    /// </summary>
    public const int DefaultPort = 34720;

    const string Key = "RelayServer";

    /// <summary>
    /// Overridden by GT2_RELAY, which is how a test machine points at a local
    /// server without anybody clicking through a settings screen.
    /// </summary>
    static readonly string FromTheEnvironment =
        Environment.GetEnvironmentVariable("GT2_RELAY") ?? "";

    static string? _override;

    public static string Address
    {
        get
        {
            if (_override is { } forced) return forced;
            if (FromTheEnvironment.Length > 0) return FromTheEnvironment;
            try { return ConfigManager.View.GetString(Key, ""); }
            catch { return ""; }
        }
        set
        {
            _override = null;
            try { ConfigManager.View.SetString(Key, value ?? ""); }
            catch { _override = value ?? ""; }
        }
    }

    /// <summary>Whether anything is configured at all.</summary>
    public static bool Configured => TryReadAddress(Address, out _);

    /// <summary>
    /// Whether GT2_RELAY is deciding this, in which case the settings screen
    /// must say so rather than offer a box that quietly does nothing. A field
    /// a player types into and which then has no effect is worse than no field
    /// at all.
    /// </summary>
    public static bool ForcedByEnvironment => FromTheEnvironment.Length > 0;

    /// <summary>
    /// Reads "host" or "host:port", where the host may be a name. Parsed here
    /// rather than where it is used, so a typing mistake is a message on a
    /// screen instead of datagrams into the void - the same reason
    /// <see cref="Session.TryReadAddress"/> exists.
    /// </summary>
    public static bool TryReadAddress(string? typed, out IPEndPoint where)
    {
        where = null!;

        string text = (typed ?? "").Trim();
        if (text.Length == 0) return false;

        int port = DefaultPort;
        string host = text;

        int colon = text.LastIndexOf(':');
        if (colon >= 0)
        {
            host = text[..colon];
            string tail = text[(colon + 1)..];
            if (!int.TryParse(tail, out port) || port is < 1 or > 65535) return false;
        }

        if (host.Length == 0) return false;

        if (IPAddress.TryParse(host, out var address))
        {
            where = new IPEndPoint(address, port);
            return true;
        }

        try
        {
            var found = Dns.GetHostAddresses(host)
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (found is null) return false;
            where = new IPEndPoint(found, port);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
