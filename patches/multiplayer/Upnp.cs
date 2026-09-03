using System.Net;
using System.Xml.Linq;

namespace GT2Port.Multiplayer;

/// <summary>
/// What to say to a home router to have it forward a port, and how to read
/// what it says back.
///
/// This is the thing a console has done since 2001 and the reason nobody
/// configures a router to play online any more. The alternative on offer here
/// was asking every player to do it by hand, which is a requirement most of
/// them cannot meet: it needs the router's password, an idea of what NAT is,
/// and an ISP that hands out a public address at all.
///
/// Only text lives here - requests to build and answers to read - so all of it
/// is testable without a router in the room. The sockets are in
/// <see cref="PortMapping"/>, which is thin on purpose, because the one thing
/// no test can settle is whether a particular router agrees.
///
/// UPnP IGD only. NAT-PMP and PCP are a second protocol for the same job and
/// would double this for the routers that speak them and nothing else, which
/// is a small enough share to leave until somebody turns up unable to host.
/// </summary>
public static class Upnp
{
    public const string MulticastAddress = "239.255.255.250";
    public const int MulticastPort = 1900;

    /// <summary>
    /// The two services that forward a port, in the order to try them. A
    /// router on cable or fibre presents the first and one on PPPoE the
    /// second; a client that only knows one works in half the houses.
    /// </summary>
    public static readonly string[] Services =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    public const string Gateway = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";

    /// <summary>Somebody else already holds this mapping.</summary>
    public const int ConflictInMappingEntry = 718;

    /// <summary>This router will not take a lease - ask for a permanent one.</summary>
    public const int OnlyPermanentLeasesSupported = 725;

    public static string SearchRequest(string searchTarget, int seconds) =>
        "M-SEARCH * HTTP/1.1\r\n"
        + $"HOST: {MulticastAddress}:{MulticastPort}\r\n"
        + "MAN: \"ssdp:discover\"\r\n"
        + $"MX: {seconds}\r\n"
        + $"ST: {searchTarget}\r\n"
        + "\r\n";

    /// <summary>
    /// Finds the LOCATION header, whatever case the manufacturer chose for it.
    /// </summary>
    public static bool TryReadLocation(string response, out Uri location)
    {
        location = null!;
        if (string.IsNullOrEmpty(response)) return false;

        foreach (var line in response.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (!line[..colon].Trim().Equals("LOCATION", StringComparison.OrdinalIgnoreCase))
                continue;

            string value = line[(colon + 1)..].Trim();
            if (Uri.TryCreate(value, UriKind.Absolute, out var found)
                && found.Scheme is "http" or "https")
            {
                location = found;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Digs the port-forwarding service out of a device description, and
    /// resolves its control URL against where the description came from -
    /// some devices give a path and some a whole address.
    /// </summary>
    public static bool TryReadService(
        string descriptionXml, Uri description, out Uri control, out string serviceType)
    {
        control = null!;
        serviceType = "";

        XDocument document;
        try { document = XDocument.Parse(descriptionXml); }
        catch (System.Xml.XmlException) { return false; }

        foreach (string wanted in Services)
        {
            foreach (var service in document.Descendants()
                         .Where(e => e.Name.LocalName == "service"))
            {
                string? type = service.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "serviceType")?.Value.Trim();
                if (type != wanted) continue;

                string? url = service.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "controlURL")?.Value.Trim();
                if (string.IsNullOrEmpty(url)) continue;

                if (!Uri.TryCreate(description, url, out var resolved)) continue;

                control = resolved;
                serviceType = type;
                return true;
            }
        }
        return false;
    }

    public static string SoapAction(string serviceType, string action) =>
        $"\"{serviceType}#{action}\"";

    static string Envelope(string body) =>
        "<?xml version=\"1.0\"?>"
        + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\""
        + " s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">"
        + $"<s:Body>{body}</s:Body>"
        + "</s:Envelope>";

    public static string AddPortMappingBody(
        string serviceType, int externalPort, int internalPort,
        string internalClient, string description, int leaseSeconds) =>
        Envelope(
            $"<u:AddPortMapping xmlns:u=\"{serviceType}\">"
            + "<NewRemoteHost></NewRemoteHost>"
            + $"<NewExternalPort>{externalPort}</NewExternalPort>"
            + "<NewProtocol>UDP</NewProtocol>"
            + $"<NewInternalPort>{internalPort}</NewInternalPort>"
            + $"<NewInternalClient>{internalClient}</NewInternalClient>"
            + "<NewEnabled>1</NewEnabled>"
            + $"<NewPortMappingDescription>{description}</NewPortMappingDescription>"
            + $"<NewLeaseDuration>{leaseSeconds}</NewLeaseDuration>"
            + "</u:AddPortMapping>");

    public static string DeletePortMappingBody(string serviceType, int externalPort) =>
        Envelope(
            $"<u:DeletePortMapping xmlns:u=\"{serviceType}\">"
            + "<NewRemoteHost></NewRemoteHost>"
            + $"<NewExternalPort>{externalPort}</NewExternalPort>"
            + "<NewProtocol>UDP</NewProtocol>"
            + "</u:DeletePortMapping>");

    public static string GetExternalAddressBody(string serviceType) =>
        Envelope($"<u:GetExternalIPAddress xmlns:u=\"{serviceType}\" />");

    public static bool TryReadFault(string xml, out int code)
    {
        code = 0;
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return false; }

        var error = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode");
        return error is not null && int.TryParse(error.Value.Trim(), out code);
    }

    public static bool TryReadExternalAddress(string xml, out IPAddress address)
    {
        address = IPAddress.None;
        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return false; }

        var found = document.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress");
        return found is not null && IPAddress.TryParse(found.Value.Trim(), out address!);
    }

    /// <summary>
    /// Whether the address the router calls "external" is itself behind
    /// somebody else's NAT - 100.64.0.0/10, which an ISP hands out when it is
    /// sharing one public address between many houses.
    ///
    /// Worth knowing because a mapping made in that case *succeeds* and does
    /// nothing: the port is open on a router that the internet cannot reach
    /// either. Saying so is the difference between a player who goes to the
    /// relay and a player who spends an evening on their router.
    /// </summary>
    public static bool IsCarrierGrade(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;
        return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
    }

    /// <summary>
    /// The lease to ask for after a refusal, or -1 when asking again is
    /// pointless. Only one refusal is worth a second attempt: a router that
    /// answers 725 wants a permanent mapping instead of a leased one.
    /// </summary>
    public static int LeaseAfter(int lease, int faultCode) =>
        faultCode == OnlyPermanentLeasesSupported && lease != 0 ? 0 : -1;
}
