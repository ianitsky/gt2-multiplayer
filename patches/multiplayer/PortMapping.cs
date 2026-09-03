using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace GT2Port.Multiplayer;

/// <summary>
/// Asks the router to forward the game's port, and gives it back afterwards.
///
/// **Off the game thread, always.** Discovery waits on a multicast reply and
/// then makes two HTTP requests to a device that may be slow or absent, which
/// is seconds in the worst case. The game thread learns the outcome by reading
/// <see cref="State"/>; it never waits for it. Nothing here is called from
/// PumpHost, which runs at interrupt rate rather than frame rate.
///
/// **Wanting it is idempotent.** <see cref="Want"/> is called from the lobby
/// every frame for as long as a room is open, and the whole of the work
/// happens on the first call.
///
/// **The mapping is given back.** Many routers refuse a leased mapping and
/// insist on a permanent one, so a port left mapped is left mapped forever -
/// and the next thing on this machine that wants it finds it taken by a game
/// that closed weeks ago.
/// </summary>
public static class PortMapping
{
    public enum How
    {
        /// <summary>Not asked for, or given back.</summary>
        Off,

        /// <summary>Asked for; the router has not answered yet.</summary>
        Trying,

        /// <summary>The port is forwarded and <see cref="Outside"/> says where.</summary>
        Mapped,

        /// <summary>No router, no UPnP, or it said no. <see cref="Why"/> says which.</summary>
        Refused,
    }

    /// <summary>How long a mapping is asked to last before it is renewed.</summary>
    const int LeaseSeconds = 3600;

    /// <summary>What shows in the router's table, so a person can recognise it.</summary>
    const string Description = "Gran Turismo 2";

    internal static bool ReadWanted() =>
        Environment.GetEnvironmentVariable("GT2_UPNP") is not "0";

    /// <summary>Off when GT2_UPNP=0, for testing what a refusal looks like.</summary>
    internal static bool Wanted { get; set; } = ReadWanted();

    static readonly Lock Gate = new();

    static volatile How _state = How.Off;
    static volatile string _why = "";
    static IPEndPoint? _outside;
    static int _attempts;

    static string _serviceType = "";
    static Uri? _control;
    static int _mappedPort;

    public static How State => _state;
    public static string Why => _why;

    /// <summary>Where the world reaches this machine, once the router has said.</summary>
    public static IPEndPoint? Outside
    {
        get { lock (Gate) return _outside; }
    }

    /// <summary>How many attempts have been started. Only a test counts them.</summary>
    internal static int Attempts
    {
        get { lock (Gate) return _attempts; }
    }

    /// <summary>
    /// Asks for the port, once. Safe to call every frame - and meant to be,
    /// because the lobby has no other moment that means "still hosting".
    /// </summary>
    public static void Want(int port)
    {
        if (!Wanted) return;

        lock (Gate)
        {
            if (_state != How.Off) return;
            _state = How.Trying;
            _why = "";
            _attempts++;
            _mappedPort = port;
        }

        // Fire and forget: nothing waits for this, and the only way its result
        // is seen is through State.
        Task.Run(() => Ask(port));
    }

    /// <summary>
    /// Gives the mapping back. Called when a room closes and when the process
    /// ends, and harmless when there was never one.
    /// </summary>
    public static void Release()
    {
        Uri? control;
        string serviceType;
        int port;

        lock (Gate)
        {
            control = _control;
            serviceType = _serviceType;
            port = _mappedPort;

            _state = How.Off;
            _why = "";
            _outside = null;
            _control = null;
            _serviceType = "";
            _mappedPort = 0;
            _attempts = 0;
        }

        if (control is null || serviceType.Length == 0 || port == 0) return;

        Task.Run(() =>
        {
            try
            {
                Post(control, serviceType, "DeletePortMapping",
                    Upnp.DeletePortMappingBody(serviceType, port));
                Console.Error.WriteLine($"[upnp] gave back udp/{port}");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                        or SocketException or IOException)
            {
                // The router went away. The lease, if there is one, expires.
            }
        });
    }

    static void Refuse(string why)
    {
        _why = why;
        _state = How.Refused;
        Console.Error.WriteLine($"[upnp] {why}");
    }

    static void Ask(int port)
    {
        try
        {
            if (!TryFindGateway(out var description))
            {
                Refuse("no router answered - UPnP may be turned off on it");
                return;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string xml = http.GetStringAsync(description).GetAwaiter().GetResult();

            if (!Upnp.TryReadService(xml, description, out var control, out string serviceType))
            {
                Refuse("the router does not offer port forwarding over UPnP");
                return;
            }

            lock (Gate)
            {
                _control = control;
                _serviceType = serviceType;
            }

            string local = LocalAddressTowards(description);

            int lease = LeaseSeconds;
            while (true)
            {
                string answer = Post(control, serviceType, "AddPortMapping",
                    Upnp.AddPortMappingBody(serviceType, port, port, local, Description, lease));

                if (!Upnp.TryReadFault(answer, out int fault)) break;

                int again = Upnp.LeaseAfter(lease, fault);
                if (again < 0)
                {
                    Refuse(fault == Upnp.ConflictInMappingEntry
                        ? $"udp/{port} is already forwarded to something else"
                        : $"the router refused with error {fault}");
                    return;
                }
                lease = again;
            }

            string outside = Post(control, serviceType, "GetExternalIPAddress",
                Upnp.GetExternalAddressBody(serviceType));

            if (Upnp.TryReadExternalAddress(outside, out var address))
            {
                if (Upnp.IsCarrierGrade(address))
                {
                    // The mapping worked and is worthless: the router's own
                    // "external" address is inside the ISP's network, so
                    // nothing on the internet can reach it either.
                    Refuse("this connection is behind carrier-grade NAT, "
                        + "so forwarding a port cannot help - use a relay");
                    return;
                }

                lock (Gate) _outside = new IPEndPoint(address, port);
            }

            _state = How.Mapped;
            Console.Error.WriteLine(
                $"[upnp] udp/{port} forwarded to {local}"
                + $"{(Outside is { } o ? $", reachable at {o}" : "")}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                    or SocketException or IOException)
        {
            Refuse($"the router did not finish answering - {e.Message}");
        }
    }

    /// <summary>
    /// Shouts on every local network and takes the first router that answers.
    ///
    /// **Every** network, not the default one. A socket bound to IPAddress.Any
    /// sends multicast out whichever interface the routing table prefers, and
    /// a development machine has several: this one has the household's card
    /// and a Hyper-V switch, and a laptop on a VPN has more. Picking one is
    /// picking wrongly some of the time, and the failure is silent - it looks
    /// exactly like a router that has UPnP turned off.
    /// </summary>
    static bool TryFindGateway(out Uri description)
    {
        description = null!;

        foreach (var local in LocalAddresses())
            if (TryFindGatewayFrom(local, out description))
                return true;

        return false;
    }

    /// <summary>Every IPv4 address this machine answers on, loopback aside.</summary>
    static List<IPAddress> LocalAddresses()
    {
        var found = new List<IPAddress>();
        try
        {
            foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (card.OperationalStatus != OperationalStatus.Up) continue;
                if (card.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var address in card.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    found.Add(address.Address);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Nothing to enumerate. The fallback below still gets a chance.
        }

        // Any is kept as a last resort rather than a first: on a machine with
        // one card it is the same thing, and on one where enumeration failed
        // it is all there is.
        found.Add(IPAddress.Any);
        return found;
    }

    static bool TryFindGatewayFrom(IPAddress local, out Uri description)
    {
        description = null!;

        UdpClient socket;
        try
        {
            socket = new UdpClient(new IPEndPoint(local, 0));
            if (!local.Equals(IPAddress.Any))
                socket.Client.SetSocketOption(SocketOptionLevel.IP,
                    SocketOptionName.MulticastInterface, local.GetAddressBytes());
        }
        catch (SocketException)
        {
            // A card that went down between being listed and being bound.
            return false;
        }

        using (socket)
        {
            socket.Client.ReceiveTimeout = 1500;

            var request = Encoding.ASCII.GetBytes(Upnp.SearchRequest(Upnp.Gateway, 2));
            var to = new IPEndPoint(IPAddress.Parse(Upnp.MulticastAddress), Upnp.MulticastPort);

            // Sent more than once because SSDP is UDP on a busy home network
            // and one lost datagram would look exactly like a router that has
            // UPnP turned off.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { socket.Send(request, request.Length, to); }
                catch (SocketException) { return false; }

                var until = DateTime.UtcNow.AddSeconds(1.5);
                while (DateTime.UtcNow < until)
                {
                    if (socket.Available == 0)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    IPEndPoint? from = null;
                    byte[] data;
                    try { data = socket.Receive(ref from); }
                    catch (SocketException) { break; }

                    if (Upnp.TryReadLocation(Encoding.ASCII.GetString(data), out description))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// This machine's address on the router's own network, which is what the
    /// mapping has to point at. Asked of the routing table rather than of the
    /// list of interfaces: a machine with a VPN, a container bridge and a
    /// wireless card has several addresses and only one of them is the one
    /// that reaches the router.
    /// </summary>
    static string LocalAddressTowards(Uri router)
    {
        using var probe = new UdpClient();
        probe.Connect(router.Host, router.Port <= 0 ? 80 : router.Port);
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Address.ToString();
    }

    static string Post(Uri control, string serviceType, string action, string body)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        using var content = new StringContent(body, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPACTION", Upnp.SoapAction(serviceType, action));

        var response = http.PostAsync(control, content).GetAwaiter().GetResult();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }
}
