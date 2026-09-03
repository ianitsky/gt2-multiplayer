# Internet Play: Rendezvous and Relay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let two people on ordinary home connections play GT2 together without either of them opening a port, by putting a small always-on server between them.

**Architecture:** A public UDP server does two jobs — it remembers which rooms exist and where their members are, and it forwards datagrams between them. Both the host and the client reach it with *outbound* connections, which every NAT allows, so nothing has to be forwarded. The server never parses a byte of the game's own protocol: a room's description travels as an opaque blob it stores and hands back, and game traffic travels as an opaque payload it forwards. On the game side the change is one seam — `LanSession` stops holding a `UdpClient` and holds an `IGameLink`, of which the relay is one implementation.

**Tech Stack:** C# / .NET 10, `System.Net.Sockets.UdpClient`, xUnit. No new NuGet packages.

## Global Constraints

- **.NET 10**, matching `GT2Port.csproj`'s `<TargetFramework>net10.0</TargetFramework>`.
- **No new NuGet dependencies** in any project this plan creates or touches.
- **The server never parses the game's protocol.** Room descriptions and game traffic are opaque `byte[]` to it. A task that has the server call `RoomState.TryDeserialise` or `LanSession.TryDeserialise` has gone wrong.
- **Envelope magic is `0xA6`, version `1`.** `0xA5` is `LanSession`'s and must not be reused.
- **The relay listens on UDP port 34720.** `34718` is LAN discovery and `34719` is the game session; the relay is a third thing and gets a third port.
- **Room codes use the alphabet `23456789ABCDEFGHJKLMNPQRSTUVWXYZ`** — 32 characters with `0`, `1`, `I` and `O` left out, so a code read aloud or over chat cannot be mistyped into a different valid one. Codes are 6 characters.
- **Caps, enforced by the server:** at most 256 rooms, at most 8 members per room, at most 1024 bytes of card, at most 1024 bytes of relayed payload, at most 1200 bytes per datagram on the wire.
- **Expiry:** a room whose host has not published for 30 seconds is forgotten; a member silent for 30 seconds is dropped from its room.
- **Never throw across a datagram.** Every parse returns a bool, in the same spirit as `RoomState.TryDeserialise` and `LanSession.TryDeserialise`: these bytes came off the internet and malformed input is normal, not exceptional.
- **Comment style matches the repository**: XML docs on public members that say *why*, recording what was measured or what went wrong, not what the line does.
- Existing tests must keep passing. The suite is 401 tests before this plan starts: `dotnet test tests/GT2Port.Tests -c Debug`.

---

## File Structure

**New project — `GT2Port.Rendezvous/`** (class library, referenced by both the game and the server)

- `GT2Port.Rendezvous.csproj` — net10.0, no dependencies at all. It must stay free of any reference to `GT2Port` or `RecompOne.Runtime`, because the server links it and must not drag in the recompiled game.
- `Envelope.cs` — the wire format between game and server: magic, version, message kinds, and a `TryRead`/`Write` pair per kind.
- `RoomCode.cs` — generating and validating the six-character code.

**New project — `GT2Relay/`** (console application, the thing that runs on the Oracle box)

- `GT2Relay.csproj` — net10.0, references `GT2Port.Rendezvous` only.
- `RoomRegistry.cs` — who exists, where they are, when they were last heard. Pure logic over an injected clock: no sockets, so it is testable without a network.
- `RelayServer.cs` — the UDP loop. Reads a datagram, asks `RoomRegistry` what it means, sends what it says to send.
- `Program.cs` — arguments, startup banner, `Ctrl+C`.
- `README.md` — how to run it on Oracle free tier, including the two firewalls.

**New tests — `tests/GT2Relay.Tests/`**

- `GT2Relay.Tests.csproj`
- `EnvelopeTests.cs` — round trips and refusals.
- `RoomCodeTests.cs`
- `RoomRegistryTests.cs` — publishing, listing, joining, expiry, caps.
- `RelayServerTests.cs` — two sockets through a real server on loopback.

**New — asking the router (Task 0, independent of everything below)**

- `patches/multiplayer/Upnp.cs` — what to say to a router and how to read the answer. Text only, so all of it is testable without one.
- `patches/multiplayer/PortMapping.cs` — the sockets, the lifecycle, and what to tell the panel. Thin on purpose.

**Modified — the game's transport seam**

- `patches/multiplayer/IGameLink.cs` (create) — the four socket operations `LanSession` actually uses.
- `patches/multiplayer/DirectLink.cs` (create) — today's `UdpClient`, unchanged behaviour.
- `patches/multiplayer/RelaySession.cs` (create) — one socket that both talks to the rendezvous and carries game traffic, so there is one NAT mapping to keep alive and one place that knows about the relay.
- `patches/multiplayer/LanSession.cs` (modify) — hold an `IGameLink` instead of a `UdpClient`.
- `patches/multiplayer/ModeHook.cs` (modify) — build a relayed session when a server address is configured.
- `patches/multiplayer/MultiplayerPanel.cs` (modify) — internet rooms in the list, a code to join by, and the host's own code shown.
- `patches/multiplayer/RelaySettings.cs` (create) — where the server address is remembered.

**Modified — tests**

- `tests/GT2Port.Tests/RelaySessionTests.cs` (create)
- `tests/GT2Port.Tests/GameLinkTests.cs` (create)

---

## Task 0: Ask the router to open the port

**Independent of every other task here.** It needs no server, no account and nothing from the player, and it can be built, shipped and judged on its own. Do it first: for every household it works in, the relay is never in the path at all — lower latency, and no bandwidth on anybody's VPS.

What it does is what a console has done since 2001: ask the router, by protocol, to forward UDP 34719 to this machine, and give it back on the way out. The player sees nothing except that hosting suddenly works.

Two things it cannot do, and both are why the relay still gets built. Under CGNAT there is no public address to map, so the router has nothing to offer. And a router with UPnP turned off will refuse. Neither is detectable in advance — both simply fail, and failing is fine here: the panel says so and the relay picks it up.

**Files:**
- Create: `patches/multiplayer/Upnp.cs` — the protocol: what to say and how to read the answer. No sockets, so all of it is testable.
- Create: `patches/multiplayer/PortMapping.cs` — the doing: discovery, the requests, the lifecycle, and what to tell the panel.
- Modify: `patches/multiplayer/ModeHook.cs`
- Modify: `patches/multiplayer/MultiplayerPanel.cs`
- Test: `tests/GT2Port.Tests/UpnpTests.cs`
- Test: `tests/GT2Port.Tests/PortMappingTests.cs`

**Interfaces:**
- Consumes: `ModeHook.SessionPortNumber`.
- Produces:
  - `static class Upnp` with:
    - `const string MulticastAddress = "239.255.255.250"`, `const int MulticastPort = 1900`
    - `static readonly string[] Services` — `WANIPConnection:1` then `WANPPPConnection:1`
    - `const int ConflictInMappingEntry = 718`, `const int OnlyPermanentLeasesSupported = 725`
    - `static string SearchRequest(string searchTarget, int seconds)`
    - `static bool TryReadLocation(string response, out Uri location)`
    - `static bool TryReadService(string descriptionXml, Uri description, out Uri control, out string serviceType)`
    - `static string SoapAction(string serviceType, string action)`
    - `static string AddPortMappingBody(string serviceType, int externalPort, int internalPort, string internalClient, string description, int leaseSeconds)`
    - `static string DeletePortMappingBody(string serviceType, int externalPort)`
    - `static string GetExternalAddressBody(string serviceType)`
    - `static bool TryReadFault(string xml, out int code)`
    - `static bool TryReadExternalAddress(string xml, out IPAddress address)`
    - `static int LeaseAfter(int lease, int faultCode)`
  - `static class PortMapping` with:
    - `enum How { Off, Trying, Mapped, Refused }`
    - `static How State { get; }`, `static IPEndPoint? Outside { get; }`, `static string Why { get; }`
    - `static void Want(int port)`, `static void Release()`

- [ ] **Step 1: Write the failing protocol tests**

Create `tests/GT2Port.Tests/UpnpTests.cs`:

```csharp
using System.Net;
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// What to say to a router, and how to read what it says back.
///
/// All of it is text, so all of it is testable without a router - which
/// matters more here than usual, because the one thing that cannot be tested
/// is whether any particular router agrees. Every sample below is the shape a
/// real device sends: the header casing varies between manufacturers, the
/// control URL is relative on some and absolute on others, and both of those
/// have broken UPnP clients before.
/// </summary>
public class UpnpTests
{
    const string Description = "http://192.168.15.1:5000/rootDesc.xml";

    static string Service(string type, string controlUrl) => $"""
        <?xml version="1.0"?>
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <device>
            <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>
            <deviceList>
              <device>
                <deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType>
                <serviceList>
                  <service>
                    <serviceType>{type}</serviceType>
                    <controlURL>{controlUrl}</controlURL>
                  </service>
                </serviceList>
              </device>
            </deviceList>
          </device>
        </root>
        """;

    [Fact]
    public void The_search_is_a_well_formed_m_search()
    {
        var request = Upnp.SearchRequest("urn:schemas-upnp-org:device:InternetGatewayDevice:1", 2);

        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", request);
        Assert.Contains($"HOST: {Upnp.MulticastAddress}:{Upnp.MulticastPort}\r\n", request);
        Assert.Contains("MAN: \"ssdp:discover\"\r\n", request);
        Assert.Contains("MX: 2\r\n", request);
        Assert.Contains("ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n", request);
        Assert.EndsWith("\r\n\r\n", request);
    }

    [Fact]
    public void The_location_is_read_out_of_a_reply()
    {
        string reply = "HTTP/1.1 200 OK\r\n"
            + "CACHE-CONTROL: max-age=120\r\n"
            + $"LOCATION: {Description}\r\n"
            + "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";

        Assert.True(Upnp.TryReadLocation(reply, out var location));
        Assert.Equal(Description, location.ToString());
    }

    /// <summary>
    /// Manufacturers disagree about capitals, and a client that only accepts
    /// one spelling works on one router and not the next.
    /// </summary>
    [Theory]
    [InlineData("LOCATION")]
    [InlineData("Location")]
    [InlineData("location")]
    public void However_the_header_is_spelled(string header)
    {
        string reply = $"HTTP/1.1 200 OK\r\n{header}: {Description}\r\n\r\n";

        Assert.True(Upnp.TryReadLocation(reply, out var location));
        Assert.Equal(Description, location.ToString());
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\n\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nLOCATION: not a url\r\n\r\n")]
    [InlineData("")]
    public void And_a_reply_with_no_usable_location_is_refused(string reply)
    {
        Assert.False(Upnp.TryReadLocation(reply, out _));
    }

    [Theory]
    [InlineData("urn:schemas-upnp-org:service:WANIPConnection:1")]
    [InlineData("urn:schemas-upnp-org:service:WANPPPConnection:1")]
    public void The_service_that_forwards_ports_is_found(string type)
    {
        Assert.True(Upnp.TryReadService(Service(type, "/ctl/IPConn"), new Uri(Description),
            out var control, out string found));

        Assert.Equal(type, found);
        Assert.Equal("http://192.168.15.1:5000/ctl/IPConn", control.ToString());
    }

    /// <summary>
    /// Some devices give an absolute control URL and some a path. Resolving it
    /// against the description's own address is what covers both.
    /// </summary>
    [Fact]
    public void An_absolute_control_url_is_taken_as_it_is()
    {
        Assert.True(Upnp.TryReadService(
            Service("urn:schemas-upnp-org:service:WANIPConnection:1",
                    "http://192.168.15.1:49152/ctl"),
            new Uri(Description), out var control, out _));

        Assert.Equal("http://192.168.15.1:49152/ctl", control.ToString());
    }

    [Fact]
    public void A_device_with_no_service_we_know_is_refused()
    {
        string xml = Service("urn:schemas-upnp-org:service:Layer3Forwarding:1", "/ctl");

        Assert.False(Upnp.TryReadService(xml, new Uri(Description), out _, out _));
    }

    [Fact]
    public void And_so_is_something_that_is_not_xml()
    {
        Assert.False(Upnp.TryReadService("<<<not xml", new Uri(Description), out _, out _));
    }

    [Fact]
    public void The_mapping_request_names_the_port_the_protocol_and_this_machine()
    {
        string body = Upnp.AddPortMappingBody(
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            externalPort: 34719, internalPort: 34719,
            internalClient: "192.168.15.7", description: "GT2", leaseSeconds: 3600);

        Assert.Contains("<u:AddPortMapping", body);
        Assert.Contains("<NewExternalPort>34719</NewExternalPort>", body);
        Assert.Contains("<NewInternalPort>34719</NewInternalPort>", body);
        Assert.Contains("<NewProtocol>UDP</NewProtocol>", body);
        Assert.Contains("<NewInternalClient>192.168.15.7</NewInternalClient>", body);
        Assert.Contains("<NewEnabled>1</NewEnabled>", body);
        Assert.Contains("<NewLeaseDuration>3600</NewLeaseDuration>", body);
    }

    [Fact]
    public void The_removal_request_names_the_port_and_the_protocol()
    {
        string body = Upnp.DeletePortMappingBody(
            "urn:schemas-upnp-org:service:WANIPConnection:1", 34719);

        Assert.Contains("<u:DeletePortMapping", body);
        Assert.Contains("<NewExternalPort>34719</NewExternalPort>", body);
        Assert.Contains("<NewProtocol>UDP</NewProtocol>", body);
    }

    [Fact]
    public void The_soap_action_is_the_service_and_the_action_in_quotes()
    {
        Assert.Equal("\"urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping\"",
            Upnp.SoapAction("urn:schemas-upnp-org:service:WANIPConnection:1", "AddPortMapping"));
    }

    [Fact]
    public void A_fault_gives_up_its_code()
    {
        string xml = """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <s:Fault>
                  <detail>
                    <UPnPError xmlns="urn:schemas-upnp-org:control-1-0">
                      <errorCode>718</errorCode>
                      <errorDescription>ConflictInMappingEntry</errorDescription>
                    </UPnPError>
                  </detail>
                </s:Fault>
              </s:Body>
            </s:Envelope>
            """;

        Assert.True(Upnp.TryReadFault(xml, out int code));
        Assert.Equal(Upnp.ConflictInMappingEntry, code);
    }

    [Fact]
    public void An_answer_that_is_not_a_fault_is_not_read_as_one()
    {
        string xml = """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <u:AddPortMappingResponse
                  xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1" />
              </s:Body>
            </s:Envelope>
            """;

        Assert.False(Upnp.TryReadFault(xml, out _));
    }

    /// <summary>
    /// The namespace declaration on the response element is not decoration:
    /// without it the prefix is undeclared, the document is not XML at all,
    /// and a parser rightly refuses the whole thing. A sample written without
    /// it here passed the "refuses a fault" test for entirely the wrong
    /// reason.
    /// </summary>
    [Fact]
    public void The_external_address_is_read_back()
    {
        string xml = """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <u:GetExternalIPAddressResponse
                  xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1">
                  <NewExternalIPAddress>189.40.11.2</NewExternalIPAddress>
                </u:GetExternalIPAddressResponse>
              </s:Body>
            </s:Envelope>
            """;

        Assert.True(Upnp.TryReadExternalAddress(xml, out var address));
        Assert.Equal(IPAddress.Parse("189.40.11.2"), address);
    }

    [Fact]
    public void An_external_address_that_is_private_is_the_sign_of_carrier_nat()
    {
        string xml = """
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
              <s:Body>
                <u:GetExternalIPAddressResponse
                  xmlns:u="urn:schemas-upnp-org:service:WANIPConnection:1">
                  <NewExternalIPAddress>100.75.3.9</NewExternalIPAddress>
                </u:GetExternalIPAddressResponse>
              </s:Body>
            </s:Envelope>
            """;

        Assert.True(Upnp.TryReadExternalAddress(xml, out var address));
        Assert.True(Upnp.IsCarrierGrade(address));
        Assert.False(Upnp.IsCarrierGrade(IPAddress.Parse("189.40.11.2")));
    }

    /// <summary>
    /// A great many routers refuse a mapping with any lease at all and answer
    /// 725. The answer is to ask again for a permanent one and remove it on
    /// the way out - which is why <see cref="PortMapping.Release"/> exists and
    /// is not merely tidy.
    /// </summary>
    [Fact]
    public void A_router_that_only_does_permanent_leases_gets_asked_for_one()
    {
        Assert.Equal(0, Upnp.LeaseAfter(3600, Upnp.OnlyPermanentLeasesSupported));
    }

    [Fact]
    public void And_any_other_fault_is_not_worth_asking_again_with()
    {
        Assert.Equal(-1, Upnp.LeaseAfter(3600, Upnp.ConflictInMappingEntry));
        Assert.Equal(-1, Upnp.LeaseAfter(0, Upnp.OnlyPermanentLeasesSupported));
    }
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/GT2Port.Tests -c Debug --filter UpnpTests`
Expected: FAIL — `Upnp` does not exist.

- [ ] **Step 3: Write `Upnp`**

Create `patches/multiplayer/Upnp.cs`:

```csharp
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
```

- [ ] **Step 4: Run it to verify it passes**

Run: `dotnet test tests/GT2Port.Tests -c Debug --filter UpnpTests`
Expected: PASS, 22 tests.

- [ ] **Step 5: Write the failing lifecycle tests**

Create `tests/GT2Port.Tests/PortMappingTests.cs`:

```csharp
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// The part that talks to a router cannot be tested without one. What can be,
/// and what has actually gone wrong in things like this, is the lifecycle
/// around it: something called every frame that starts a second attempt each
/// time is a machine sending a hundred SOAP requests a second at its own
/// router.
/// </summary>
public class PortMappingTests
{
    [Fact]
    public void It_starts_off()
    {
        PortMapping.Release();

        Assert.Equal(PortMapping.How.Off, PortMapping.State);
        Assert.Null(PortMapping.Outside);
    }

    /// <summary>
    /// Want is called from the lobby, every frame, for as long as a room is
    /// open. The second call and the ten-thousandth must do nothing.
    /// </summary>
    [Fact]
    public void Wanting_it_over_and_over_only_tries_once()
    {
        PortMapping.Release();

        for (int i = 0; i < 500; i++) PortMapping.Want(34719);

        Assert.Equal(1, PortMapping.Attempts);
        Assert.NotEqual(PortMapping.How.Off, PortMapping.State);

        PortMapping.Release();
    }

    [Fact]
    public void Releasing_something_that_was_never_mapped_is_nothing()
    {
        PortMapping.Release();
        PortMapping.Release();

        Assert.Equal(PortMapping.How.Off, PortMapping.State);
    }

    /// <summary>
    /// And after letting go, wanting it again is a fresh attempt - a player
    /// who closes a room and opens another must not be stuck with whatever
    /// the first one concluded.
    /// </summary>
    [Fact]
    public void And_after_letting_go_it_will_try_again()
    {
        PortMapping.Release();
        PortMapping.Want(34719);
        PortMapping.Release();
        PortMapping.Want(34719);

        Assert.Equal(1, PortMapping.Attempts);

        PortMapping.Release();
    }

    [Fact]
    public void Switched_off_it_never_tries()
    {
        PortMapping.Release();
        Environment.SetEnvironmentVariable("GT2_UPNP", "0");
        try
        {
            PortMapping.Wanted = PortMapping.ReadWanted();
            PortMapping.Want(34719);

            Assert.Equal(PortMapping.How.Off, PortMapping.State);
            Assert.Equal(0, PortMapping.Attempts);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GT2_UPNP", null);
            PortMapping.Wanted = PortMapping.ReadWanted();
            PortMapping.Release();
        }
    }
}
```

- [ ] **Step 6: Run it to verify it fails**

Run: `dotnet test tests/GT2Port.Tests -c Debug --filter PortMappingTests`
Expected: FAIL — `PortMapping` does not exist.

- [ ] **Step 7: Write `PortMapping`**

Create `patches/multiplayer/PortMapping.cs`:

```csharp
using System.Net;
using System.Net.Http;
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
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
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
```

- [ ] **Step 8: Run it to verify it passes**

Run: `dotnet test tests/GT2Port.Tests -c Debug --filter PortMappingTests`
Expected: PASS, 5 tests.

- [ ] **Step 9: Ask for the port while a room is open**

In `patches/multiplayer/ModeHook.cs`, in the same per-frame place that ticks discovery, alongside the existing phase handling:

```csharp
                // A room is open, so the port should be. Idempotent: the work
                // happens on the first frame of hosting and the rest are free.
                if (_session.Phase == SessionPhase.Hosting) PortMapping.Want(SessionPort);
                else PortMapping.Release();
```

And where the process is shutting down — the same place `_lanSession?.Dispose()` happens when the panel closes the session for good — add:

```csharp
        PortMapping.Release();
```

A mapping left behind outlives the game, because a router that insisted on a permanent one keeps it until something removes it.

- [ ] **Step 10: Say so on the screen**

In `patches/multiplayer/MultiplayerPanel.cs`, in `DrawLobby`, replace the host's line:

```csharp
        if (_session.Phase == SessionPhase.Hosting)
        {
            ImGui.TextDisabled($"Others join at your address, port {ModeHook.SessionPortNumber}");
```

with one that says what the router actually did:

```csharp
        if (_session.Phase == SessionPhase.Hosting)
        {
            // What to tell people, in the order of how much is known. A
            // mapped port means a real address to read out; anything else
            // means "your address", which is what this said before the router
            // was ever asked.
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
```

keeping the rest of that block - the datagram counter added for the knocking diagnosis - as it is.

- [ ] **Step 11: Build and run everything**

Run: `dotnet build -c Debug`
Expected: 0 errors.

Run: `dotnet test tests/GT2Port.Tests -c Debug`
Expected: PASS — 25 more than before this task (20 from `UpnpTests`, 5 from `PortMappingTests`).

- [ ] **Step 12: Try it against a real router**

> **Changed while executing.** Discovery was written against `IPAddress.Any`,
> which sends multicast out whichever interface the routing table prefers. The
> machine this was first run on has two - the household's card and a Hyper-V
> switch - and a laptop on a VPN has more, so one of them is silently never
> asked. `TryFindGateway` above now walks every interface. It did not change
> the outcome on that machine (its router answers nothing at all, on either
> interface), which is why it is worth writing down: the bug would have hidden
> behind a genuine refusal.

This is the step no test replaces. On a machine behind a home router:

```bash
./bin/Debug/net10.0/GT2Port.exe
```

Create a room and read the log. One of:

- `[upnp] udp/34719 forwarded to 192.168.15.7, reachable at 189.40.11.2:34719` — it worked, and that address is what another player types into "Join by address".
- `[upnp] no router answered - UPnP may be turned off on it` — turn UPnP on in the router and try again to tell that apart from a router that has none.
- `[upnp] this connection is behind carrier-grade NAT...` — nothing on this connection can be reached from outside, and the relay is the only way. Worth knowing for certain.

Then have somebody outside the network join at the address it printed. If they get in, the relay is not needed for this household at all.

- [ ] **Step 13: Commit**

```bash
git add patches/multiplayer/Upnp.cs patches/multiplayer/PortMapping.cs patches/multiplayer/ModeHook.cs patches/multiplayer/MultiplayerPanel.cs tests/GT2Port.Tests/UpnpTests.cs tests/GT2Port.Tests/PortMappingTests.cs
git commit -m "Ask the router for the port instead of asking the player"
```

---

## Task 1: The envelope

**Files:**
- Create: `GT2Port.Rendezvous/GT2Port.Rendezvous.csproj`
- Create: `GT2Port.Rendezvous/Envelope.cs`
- Create: `GT2Port.Rendezvous/RoomCode.cs`
- Create: `tests/GT2Relay.Tests/GT2Relay.Tests.csproj`
- Test: `tests/GT2Relay.Tests/EnvelopeTests.cs`
- Test: `tests/GT2Relay.Tests/RoomCodeTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `Envelope.Kind`, nested in `Envelope` and referred to as `Envelope.Kind.Publish`: `enum Kind : byte { Publish = 1, Published = 2, List = 3, RoomCard = 4, Join = 5, JoinByCode = 6, Joined = 7, Peer = 8, Relay = 9, Relayed = 10, NoRoom = 11, Leave = 12 }`
  - `static class Envelope` with `const byte Magic = 0xA6`, `const byte Version = 1`, `const int MaxCard = 1024`, `const int MaxPayload = 1024`, `const int MaxDatagram = 1200`, `const int CodeLength = 6`
  - `static bool TryReadKind(ReadOnlySpan<byte> data, out Kind kind)`
  - `static byte[] WritePublish(Guid roomId, bool listed, ReadOnlySpan<byte> card)`
  - `static bool TryReadPublish(ReadOnlySpan<byte> data, out Guid roomId, out bool listed, out byte[] card)`
  - `static byte[] WritePublished(Guid roomId, string code)`
  - `static bool TryReadPublished(ReadOnlySpan<byte> data, out Guid roomId, out string code)`
  - `static byte[] WriteList()`
  - `static byte[] WriteRoomCard(Guid roomId, string code, ReadOnlySpan<byte> card)`
  - `static bool TryReadRoomCard(ReadOnlySpan<byte> data, out Guid roomId, out string code, out byte[] card)`
  - `static byte[] WriteJoin(Guid roomId)`
  - `static bool TryReadJoin(ReadOnlySpan<byte> data, out Guid roomId)`
  - `static byte[] WriteJoinByCode(string code)`
  - `static bool TryReadJoinByCode(ReadOnlySpan<byte> data, out string code)`
  - `static byte[] WriteJoined(Guid roomId)`
  - `static bool TryReadJoined(ReadOnlySpan<byte> data, out Guid roomId)`
  - `static byte[] WritePeer(Guid roomId, IPEndPoint peer)`
  - `static bool TryReadPeer(ReadOnlySpan<byte> data, out Guid roomId, out IPEndPoint peer)`
  - `static byte[] WriteRelay(Guid roomId, IPEndPoint to, ReadOnlySpan<byte> payload)`
  - `static bool TryReadRelay(ReadOnlySpan<byte> data, out Guid roomId, out IPEndPoint to, out byte[] payload)`
  - `static byte[] WriteRelayed(Guid roomId, IPEndPoint from, ReadOnlySpan<byte> payload)`
  - `static bool TryReadRelayed(ReadOnlySpan<byte> data, out Guid roomId, out IPEndPoint from, out byte[] payload)`
  - `static byte[] WriteNoRoom()`
  - `static byte[] WriteLeave(Guid roomId)`
  - `static bool TryReadLeave(ReadOnlySpan<byte> data, out Guid roomId)`
  - `static class RoomCode` with `const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ"`, `static string Next(Random random)`, `static bool IsWellFormed(string code)`, `static string Tidy(string typed)`

- [ ] **Step 1: Create the library project**

Create `GT2Port.Rendezvous/GT2Port.Rendezvous.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
    </PropertyGroup>

</Project>
```

This project must never gain a `ProjectReference`. The relay links it and the relay must not drag the recompiled game onto a server.

- [ ] **Step 2: Create the test project**

Create `tests/GT2Relay.Tests/GT2Relay.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <IsPackable>false</IsPackable>
    </PropertyGroup>

    <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
        <PackageReference Include="xunit" Version="2.9.2" />
        <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    </ItemGroup>

    <ItemGroup>
        <ProjectReference Include="../../GT2Port.Rendezvous/GT2Port.Rendezvous.csproj" />
    </ItemGroup>

</Project>
```

If `tests/GT2Port.Tests/GT2Port.Tests.csproj` pins different versions of those three packages, copy its versions instead of these — one set of versions across the repository.

- [ ] **Step 3: Write the failing round-trip tests**

Create `tests/GT2Relay.Tests/EnvelopeTests.cs`:

```csharp
using System.Net;
using GT2Port.Rendezvous;
using Xunit;

namespace GT2Relay.Tests;

/// <summary>
/// The format between a player and the rendezvous server.
///
/// Every one of these arrives off the open internet, so the shape that matters
/// is not "does a good message survive" but "does a bad one get refused" - a
/// length field that promises more bytes than the datagram carries is the
/// oldest way to turn a parser into a crash.
/// </summary>
public class EnvelopeTests
{
    static readonly Guid Room = Guid.Parse("11111111-2222-3333-4444-555555555555");
    static readonly IPEndPoint Somewhere = new(IPAddress.Parse("203.0.113.7"), 51234);

    [Fact]
    public void Round_trips_a_publish()
    {
        byte[] card = [1, 2, 3, 4, 5];

        var bytes = Envelope.WritePublish(Room, listed: true, card);

        Assert.True(Envelope.TryReadKind(bytes, out var kind));
        Assert.Equal(Envelope.Kind.Publish, kind);
        Assert.True(Envelope.TryReadPublish(bytes, out var room, out bool listed, out var back));
        Assert.Equal(Room, room);
        Assert.True(listed);
        Assert.Equal(card, back);
    }

    [Fact]
    public void Round_trips_an_unlisted_publish()
    {
        var bytes = Envelope.WritePublish(Room, listed: false, [9]);

        Assert.True(Envelope.TryReadPublish(bytes, out _, out bool listed, out _));
        Assert.False(listed);
    }

    [Fact]
    public void Round_trips_a_room_card()
    {
        byte[] card = [7, 7, 7];

        var bytes = Envelope.WriteRoomCard(Room, "AB23CD", card);

        Assert.True(Envelope.TryReadRoomCard(bytes, out var room, out string code, out var back));
        Assert.Equal(Room, room);
        Assert.Equal("AB23CD", code);
        Assert.Equal(card, back);
    }

    [Fact]
    public void Round_trips_a_peer()
    {
        var bytes = Envelope.WritePeer(Room, Somewhere);

        Assert.True(Envelope.TryReadPeer(bytes, out var room, out var peer));
        Assert.Equal(Room, room);
        Assert.Equal(Somewhere, peer);
    }

    [Fact]
    public void Round_trips_a_relay_and_what_it_becomes()
    {
        byte[] payload = [0xA5, 3, 4, 5];

        var out_ = Envelope.WriteRelay(Room, Somewhere, payload);
        Assert.True(Envelope.TryReadRelay(out_, out var room, out var to, out var sent));
        Assert.Equal(Room, room);
        Assert.Equal(Somewhere, to);
        Assert.Equal(payload, sent);

        var back = Envelope.WriteRelayed(Room, Somewhere, sent);
        Assert.True(Envelope.TryReadRelayed(back, out var room2, out var from, out var got));
        Assert.Equal(Room, room2);
        Assert.Equal(Somewhere, from);
        Assert.Equal(payload, got);
    }

    [Fact]
    public void Round_trips_the_short_messages()
    {
        Assert.True(Envelope.TryReadJoin(Envelope.WriteJoin(Room), out var a));
        Assert.Equal(Room, a);

        Assert.True(Envelope.TryReadJoined(Envelope.WriteJoined(Room), out var b));
        Assert.Equal(Room, b);

        Assert.True(Envelope.TryReadLeave(Envelope.WriteLeave(Room), out var c));
        Assert.Equal(Room, c);

        Assert.True(Envelope.TryReadJoinByCode(Envelope.WriteJoinByCode("HJ4KMN"), out string code));
        Assert.Equal("HJ4KMN", code);

        Assert.True(Envelope.TryReadKind(Envelope.WriteList(), out var list));
        Assert.Equal(Envelope.Kind.List, list);

        Assert.True(Envelope.TryReadKind(Envelope.WriteNoRoom(), out var none));
        Assert.Equal(Envelope.Kind.NoRoom, none);
    }

    [Fact]
    public void Refuses_a_datagram_that_is_not_ours()
    {
        Assert.False(Envelope.TryReadKind([0xA5, 1, 1], out _));
    }

    [Fact]
    public void Refuses_a_version_it_does_not_know()
    {
        Assert.False(Envelope.TryReadKind([Envelope.Magic, 99, 1], out _));
    }

    [Fact]
    public void Refuses_an_empty_datagram()
    {
        Assert.False(Envelope.TryReadKind([], out _));
        Assert.False(Envelope.TryReadPublish([], out _, out _, out _));
    }

    /// <summary>
    /// A length that promises more than the datagram holds. This is the case
    /// that turns a parser into a crash, and it is the one a hostile sender
    /// reaches for first.
    /// </summary>
    [Fact]
    public void Refuses_a_card_longer_than_the_datagram()
    {
        var bytes = Envelope.WritePublish(Room, listed: true, [1, 2, 3, 4]);
        var truncated = bytes[..^2];

        Assert.False(Envelope.TryReadPublish(truncated, out _, out _, out _));
    }

    [Fact]
    public void Refuses_a_relay_payload_longer_than_the_datagram()
    {
        var bytes = Envelope.WriteRelay(Room, Somewhere, [1, 2, 3, 4]);

        Assert.False(Envelope.TryReadRelay(bytes[..^1], out _, out _, out _));
    }

    [Fact]
    public void Refuses_to_write_more_than_it_will_carry()
    {
        Assert.Throws<ArgumentException>(
            () => Envelope.WritePublish(Room, true, new byte[Envelope.MaxCard + 1]));
        Assert.Throws<ArgumentException>(
            () => Envelope.WriteRelay(Room, Somewhere, new byte[Envelope.MaxPayload + 1]));
    }

    /// <summary>
    /// Reading one kind out of another's bytes must fail rather than return
    /// something plausible: every kind is a different shape and a parser that
    /// ignores the kind byte will read a room id out of a payload.
    /// </summary>
    [Fact]
    public void Refuses_to_read_one_kind_as_another()
    {
        var join = Envelope.WriteJoin(Room);

        Assert.False(Envelope.TryReadPublish(join, out _, out _, out _));
        Assert.False(Envelope.TryReadRelayed(join, out _, out _, out _));
    }

    [Fact]
    public void Refuses_a_code_that_is_the_wrong_length()
    {
        Assert.Throws<ArgumentException>(() => Envelope.WriteJoinByCode("ABC"));
    }
}
```

Create `tests/GT2Relay.Tests/RoomCodeTests.cs`:

```csharp
using GT2Port.Rendezvous;
using Xunit;

namespace GT2Relay.Tests;

/// <summary>
/// The code a player types to reach a room nobody listed.
///
/// It is read off one screen and typed into another, sometimes aloud, so the
/// alphabet leaves out the characters that get confused for one another. A
/// code that can be mistyped into a *different valid code* sends somebody into
/// a stranger's room; one that is simply refused sends them back to the box.
/// </summary>
public class RoomCodeTests
{
    [Fact]
    public void A_code_is_six_characters_of_the_alphabet()
    {
        var random = new Random(1234);

        for (int i = 0; i < 200; i++)
        {
            string code = RoomCode.Next(random);
            Assert.Equal(6, code.Length);
            Assert.All(code, c => Assert.Contains(c, RoomCode.Alphabet));
            Assert.True(RoomCode.IsWellFormed(code));
        }
    }

    [Theory]
    [InlineData('0')]
    [InlineData('1')]
    [InlineData('I')]
    [InlineData('O')]
    public void The_confusable_characters_are_not_in_it(char c)
    {
        Assert.DoesNotContain(c, RoomCode.Alphabet);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("ABC", false)]
    [InlineData("ABCDEFG", false)]
    [InlineData("ABCDE0", false)]
    [InlineData("ABCDEF", true)]
    [InlineData("234567", true)]
    public void Well_formed_is_length_and_alphabet(string code, bool ok)
    {
        Assert.Equal(ok, RoomCode.IsWellFormed(code));
    }

    /// <summary>
    /// What a person types is not what the wire carries: spaces, a lowercase
    /// keyboard, and the dash people put in the middle of anything six
    /// characters long.
    /// </summary>
    [Theory]
    [InlineData("ab23cd", "AB23CD")]
    [InlineData("  AB23CD  ", "AB23CD")]
    [InlineData("AB2-3CD", "AB23CD")]
    [InlineData("ab2 3cd", "AB23CD")]
    public void Tidy_makes_what_was_typed_into_what_was_meant(string typed, string want)
    {
        Assert.Equal(want, RoomCode.Tidy(typed));
    }

    [Fact]
    public void Tidy_leaves_something_that_cannot_be_a_code_alone_enough_to_be_refused()
    {
        Assert.False(RoomCode.IsWellFormed(RoomCode.Tidy("hello there")));
    }
}
```

- [ ] **Step 4: Run the tests to verify they fail**

Run: `dotnet test tests/GT2Relay.Tests -c Debug`
Expected: FAIL — the project does not compile, `Envelope` and `RoomCode` do not exist.

- [ ] **Step 5: Write `RoomCode`**

Create `GT2Port.Rendezvous/RoomCode.cs`:

```csharp
namespace GT2Port.Rendezvous;

/// <summary>
/// The six characters that stand for a room nobody listed.
///
/// A room's real name is a <see cref="Guid"/>, which is the right thing for a
/// wire and the wrong thing for a person: nobody reads one out over a call.
/// The code is what a person carries between two screens.
///
/// The alphabet leaves out 0, 1, I and O. Not for tidiness - because a code
/// that can be mistyped into a *different valid code* puts somebody in a
/// stranger's room, while one that cannot be mistyped into anything valid puts
/// them back in the box with a message. Thirty-two characters over six places
/// is still a thousand million codes, which is far more than a hobby server
/// will ever hold at once.
/// </summary>
public static class RoomCode
{
    public const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    public const int Length = 6;

    public static string Next(Random random)
    {
        Span<char> code = stackalloc char[Length];
        for (int i = 0; i < Length; i++) code[i] = Alphabet[random.Next(Alphabet.Length)];
        return new string(code);
    }

    public static bool IsWellFormed(string? code)
    {
        if (code is null || code.Length != Length) return false;
        foreach (char c in code)
            if (!Alphabet.Contains(c)) return false;
        return true;
    }

    /// <summary>
    /// What was typed, as the wire wants it: upper case, and without the
    /// spaces and dashes people put through anything six characters long.
    ///
    /// This does not judge - it tidies. Whatever comes out still has to pass
    /// <see cref="IsWellFormed"/>, so "hello there" tidies to something that is
    /// then refused rather than sent.
    /// </summary>
    public static string Tidy(string? typed)
    {
        if (string.IsNullOrEmpty(typed)) return "";

        var kept = new System.Text.StringBuilder(Length);
        foreach (char c in typed.ToUpperInvariant())
        {
            if (c is ' ' or '-' or '_') continue;
            kept.Append(c);
        }
        return kept.ToString();
    }
}
```

- [ ] **Step 6: Write `Envelope`**

Create `GT2Port.Rendezvous/Envelope.cs`:

```csharp
using System.Buffers.Binary;
using System.Net;

namespace GT2Port.Rendezvous;

/// <summary>
/// What a player and the rendezvous server say to each other.
///
/// Deliberately not the game's own format. The server stores a room's
/// description and forwards a room's traffic without looking inside either:
/// the description is whatever <c>RoomState.Serialise</c> produced and the
/// traffic is whatever <c>LanSession</c> sent, and both are just a length and
/// some bytes here. That is what lets the game's protocol change - a new field
/// in a room, a new kind of message during a race - without anybody having to
/// redeploy a server, and it is why this project has no reference to the game
/// at all.
///
/// Every read is a <c>TryRead</c> and never throws. These bytes arrive from
/// the open internet, where a length field that promises more than the
/// datagram carries is not an edge case but the first thing anybody tries.
/// Writes do throw, because a message this program built too large is a bug in
/// this program.
/// </summary>
public static class Envelope
{
    /// <summary>
    /// Distinct from LanSession's 0xA5 on purpose. The two protocols share a
    /// socket in <c>RelaySession</c>, so a byte that told them apart only by
    /// context would be a byte that eventually gets it wrong.
    /// </summary>
    public const byte Magic = 0xA6;

    public const byte Version = 1;

    /// <summary>Magic, version, kind.</summary>
    public const int HeaderBytes = 3;

    public const int MaxCard = 1024;
    public const int MaxPayload = 1024;

    /// <summary>
    /// What a datagram may total. Comfortably inside the smallest path MTU
    /// anybody still meets, so nothing this sends is ever fragmented - a
    /// fragmented UDP datagram is one that a middlebox somewhere will drop.
    /// </summary>
    public const int MaxDatagram = 1200;

    public const int CodeLength = RoomCode.Length;

    public enum Kind : byte
    {
        Publish = 1,
        Published = 2,
        List = 3,
        RoomCard = 4,
        Join = 5,
        JoinByCode = 6,
        Joined = 7,
        Peer = 8,
        Relay = 9,
        Relayed = 10,
        NoRoom = 11,
        Leave = 12,
    }

    public static bool TryReadKind(ReadOnlySpan<byte> data, out Kind kind)
    {
        kind = default;
        if (data.Length < HeaderBytes) return false;
        if (data[0] != Magic || data[1] != Version) return false;
        if (!Enum.IsDefined((Kind)data[2])) return false;
        kind = (Kind)data[2];
        return true;
    }

    static bool Is(ReadOnlySpan<byte> data, Kind kind) =>
        TryReadKind(data, out var got) && got == kind;

    static byte[] Head(Kind kind, int extra)
    {
        var bytes = new byte[HeaderBytes + extra];
        bytes[0] = Magic;
        bytes[1] = Version;
        bytes[2] = (byte)kind;
        return bytes;
    }

    // ---- room ids ----

    static void WriteGuid(Span<byte> to, Guid id) => id.TryWriteBytes(to);

    static bool TryReadGuid(ReadOnlySpan<byte> data, int at, out Guid id)
    {
        id = Guid.Empty;
        if (data.Length < at + 16) return false;
        id = new Guid(data.Slice(at, 16));
        return true;
    }

    // ---- endpoints ----
    //
    // Four bytes and a port: IPv4 only. An IPv6 peer reaching an IPv4 relay
    // has no NAT to punch through in the first place, and mixing the two here
    // would mean a variable-length field in the middle of every relayed
    // datagram for a case this stage does not serve.

    const int EndPointBytes = 6;

    static void WriteEndPoint(Span<byte> to, IPEndPoint where)
    {
        Span<byte> address = stackalloc byte[4];
        if (!where.Address.TryWriteBytes(address, out int written) || written != 4)
            throw new ArgumentException("only IPv4 endpoints travel in an envelope", nameof(where));
        address.CopyTo(to);
        BinaryPrimitives.WriteUInt16LittleEndian(to[4..], (ushort)where.Port);
    }

    static bool TryReadEndPoint(ReadOnlySpan<byte> data, int at, out IPEndPoint where)
    {
        where = new IPEndPoint(IPAddress.Any, 0);
        if (data.Length < at + EndPointBytes) return false;
        var address = new IPAddress(data.Slice(at, 4));
        ushort port = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at + 4, 2));
        where = new IPEndPoint(address, port);
        return true;
    }

    // ---- codes ----

    static void WriteCode(Span<byte> to, string code)
    {
        if (!RoomCode.IsWellFormed(code))
            throw new ArgumentException($"\"{code}\" is not a room code", nameof(code));
        for (int i = 0; i < CodeLength; i++) to[i] = (byte)code[i];
    }

    static bool TryReadCode(ReadOnlySpan<byte> data, int at, out string code)
    {
        code = "";
        if (data.Length < at + CodeLength) return false;

        Span<char> chars = stackalloc char[CodeLength];
        for (int i = 0; i < CodeLength; i++) chars[i] = (char)data[at + i];

        string read = new(chars);
        if (!RoomCode.IsWellFormed(read)) return false;

        code = read;
        return true;
    }

    // ---- blobs ----

    static void WriteBlob(Span<byte> to, ReadOnlySpan<byte> blob)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(to, (ushort)blob.Length);
        blob.CopyTo(to[2..]);
    }

    static bool TryReadBlob(ReadOnlySpan<byte> data, int at, int most, out byte[] blob)
    {
        blob = [];
        if (data.Length < at + 2) return false;

        int length = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(at, 2));
        if (length > most) return false;
        if (data.Length < at + 2 + length) return false;

        blob = data.Slice(at + 2, length).ToArray();
        return true;
    }

    // ---- Publish / Published ----

    public static byte[] WritePublish(Guid roomId, bool listed, ReadOnlySpan<byte> card)
    {
        if (card.Length > MaxCard)
            throw new ArgumentException($"a card is at most {MaxCard} bytes", nameof(card));

        var bytes = Head(Kind.Publish, 16 + 1 + 2 + card.Length);
        WriteGuid(bytes.AsSpan(HeaderBytes), roomId);
        bytes[HeaderBytes + 16] = (byte)(listed ? 1 : 0);
        WriteBlob(bytes.AsSpan(HeaderBytes + 17), card);
        return bytes;
    }

    public static bool TryReadPublish(
        ReadOnlySpan<byte> data, out Guid roomId, out bool listed, out byte[] card)
    {
        roomId = Guid.Empty;
        listed = false;
        card = [];

        if (!Is(data, Kind.Publish)) return false;
        if (!TryReadGuid(data, HeaderBytes, out roomId)) return false;
        if (data.Length < HeaderBytes + 17) return false;

        listed = data[HeaderBytes + 16] != 0;
        return TryReadBlob(data, HeaderBytes + 17, MaxCard, out card);
    }

    public static byte[] WritePublished(Guid roomId, string code)
    {
        var bytes = Head(Kind.Published, 16 + CodeLength);
        WriteGuid(bytes.AsSpan(HeaderBytes), roomId);
        WriteCode(bytes.AsSpan(HeaderBytes + 16), code);
        return bytes;
    }

    public static bool TryReadPublished(ReadOnlySpan<byte> data, out Guid roomId, out string code)
    {
        roomId = Guid.Empty;
        code = "";
        if (!Is(data, Kind.Published)) return false;
        if (!TryReadGuid(data, HeaderBytes, out roomId)) return false;
        return TryReadCode(data, HeaderBytes + 16, out code);
    }

    // ---- List / RoomCard ----

    public static byte[] WriteList() => Head(Kind.List, 0);

    /// <summary>
    /// One room, one datagram. A listing that packed every room into one
    /// message would be a message whose size depends on how popular the server
    /// is that evening, and the first busy night would be the night it grew
    /// past what a datagram carries.
    /// </summary>
    public static byte[] WriteRoomCard(Guid roomId, string code, ReadOnlySpan<byte> card)
    {
        if (card.Length > MaxCard)
            throw new ArgumentException($"a card is at most {MaxCard} bytes", nameof(card));

        var bytes = Head(Kind.RoomCard, 16 + CodeLength + 2 + card.Length);
        WriteGuid(bytes.AsSpan(HeaderBytes), roomId);
        WriteCode(bytes.AsSpan(HeaderBytes + 16), code);
        WriteBlob(bytes.AsSpan(HeaderBytes + 16 + CodeLength), card);
        return bytes;
    }

    public static bool TryReadRoomCard(
        ReadOnlySpan<byte> data, out Guid roomId, out string code, out byte[] card)
    {
        roomId = Guid.Empty;
        code = "";
        card = [];

        if (!Is(data, Kind.RoomCard)) return false;
        if (!TryReadGuid(data, HeaderBytes, out roomId)) return false;
        if (!TryReadCode(data, HeaderBytes + 16, out code)) return false;
        return TryReadBlob(data, HeaderBytes + 16 + CodeLength, MaxCard, out card);
    }

    // ---- Join / JoinByCode / Joined / Leave ----

    static byte[] WriteRoomOnly(Kind kind, Guid roomId)
    {
        var bytes = Head(kind, 16);
        WriteGuid(bytes.AsSpan(HeaderBytes), roomId);
        return bytes;
    }

    static bool TryReadRoomOnly(ReadOnlySpan<byte> data, Kind kind, out Guid roomId)
    {
        roomId = Guid.Empty;
        if (!Is(data, kind)) return false;
        return TryReadGuid(data, HeaderBytes, out roomId);
    }

    public static byte[] WriteJoin(Guid roomId) => WriteRoomOnly(Kind.Join, roomId);

    public static bool TryReadJoin(ReadOnlySpan<byte> data, out Guid roomId) =>
        TryReadRoomOnly(data, Kind.Join, out roomId);

    public static byte[] WriteJoined(Guid roomId) => WriteRoomOnly(Kind.Joined, roomId);

    public static bool TryReadJoined(ReadOnlySpan<byte> data, out Guid roomId) =>
        TryReadRoomOnly(data, Kind.Joined, out roomId);

    public static byte[] WriteLeave(Guid roomId) => WriteRoomOnly(Kind.Leave, roomId);

    public static bool TryReadLeave(ReadOnlySpan<byte> data, out Guid roomId) =>
        TryReadRoomOnly(data, Kind.Leave, out roomId);

    public static byte[] WriteJoinByCode(string code)
    {
        var bytes = Head(Kind.JoinByCode, CodeLength);
        WriteCode(bytes.AsSpan(HeaderBytes), code);
        return bytes;
    }

    public static bool TryReadJoinByCode(ReadOnlySpan<byte> data, out string code)
    {
        code = "";
        if (!Is(data, Kind.JoinByCode)) return false;
        return TryReadCode(data, HeaderBytes, out code);
    }

    public static byte[] WriteNoRoom() => Head(Kind.NoRoom, 0);

    // ---- Peer ----

    public static byte[] WritePeer(Guid roomId, IPEndPoint peer)
    {
        var bytes = Head(Kind.Peer, 16 + EndPointBytes);
        WriteGuid(bytes.AsSpan(HeaderBytes), roomId);
        WriteEndPoint(bytes.AsSpan(HeaderBytes + 16), peer);
        return bytes;
    }

    public static bool TryReadPeer(ReadOnlySpan<byte> data, out Guid roomId, out IPEndPoint peer)
    {
        roomId = Guid.Empty;
        peer = new IPEndPoint(IPAddress.Any, 0);
        if (!Is(data, Kind.Peer)) return false;
        if (!TryReadGuid(data, HeaderBytes, out roomId)) return false;
        return TryReadEndPoint(data, HeaderBytes + 16, out peer);
    }

    // ---- Relay / Relayed ----

    static byte[] WriteCarried(Kind kind, Guid roomId, IPEndPoint who, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayload)
            throw new ArgumentException($"a payload is at most {MaxPayload} bytes", nameof(payload));

        var bytes = Head(kind, 16 + EndPointBytes + 2 + payload.Length);
        WriteGuid(bytes.AsSpan(HeaderBytes), roomId);
        WriteEndPoint(bytes.AsSpan(HeaderBytes + 16), who);
        WriteBlob(bytes.AsSpan(HeaderBytes + 16 + EndPointBytes), payload);
        return bytes;
    }

    static bool TryReadCarried(
        ReadOnlySpan<byte> data, Kind kind,
        out Guid roomId, out IPEndPoint who, out byte[] payload)
    {
        roomId = Guid.Empty;
        who = new IPEndPoint(IPAddress.Any, 0);
        payload = [];

        if (!Is(data, kind)) return false;
        if (!TryReadGuid(data, HeaderBytes, out roomId)) return false;
        if (!TryReadEndPoint(data, HeaderBytes + 16, out who)) return false;
        return TryReadBlob(data, HeaderBytes + 16 + EndPointBytes, MaxPayload, out payload);
    }

    public static byte[] WriteRelay(Guid roomId, IPEndPoint to, ReadOnlySpan<byte> payload) =>
        WriteCarried(Kind.Relay, roomId, to, payload);

    public static bool TryReadRelay(
        ReadOnlySpan<byte> data, out Guid roomId, out IPEndPoint to, out byte[] payload) =>
        TryReadCarried(data, Kind.Relay, out roomId, out to, out payload);

    public static byte[] WriteRelayed(Guid roomId, IPEndPoint from, ReadOnlySpan<byte> payload) =>
        WriteCarried(Kind.Relayed, roomId, from, payload);

    public static bool TryReadRelayed(
        ReadOnlySpan<byte> data, out Guid roomId, out IPEndPoint from, out byte[] payload) =>
        TryReadCarried(data, Kind.Relayed, out roomId, out from, out payload);
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test tests/GT2Relay.Tests -c Debug`
Expected: PASS, 30 tests - all of `EnvelopeTests` and `RoomCodeTests`.

- [ ] **Step 8: Check nothing else broke**

Run: `dotnet test tests/GT2Port.Tests -c Debug`
Expected: PASS, 401 tests.

- [ ] **Step 9: Commit**

```bash
git add GT2Port.Rendezvous tests/GT2Relay.Tests
git commit -m "Give the game and a server a format to meet in"
```

---

## Task 2: The room registry

Who exists, where they are, and when they were last heard from. All of the server's decisions live here, over an injected clock and an injected `Random`, so every one of them can be tested without a socket and without waiting thirty seconds for something to expire.

**Files:**
- Create: `GT2Relay/GT2Relay.csproj`
- Create: `GT2Relay/RoomRegistry.cs`
- Test: `tests/GT2Relay.Tests/RoomRegistryTests.cs`
- Modify: `tests/GT2Relay.Tests/GT2Relay.Tests.csproj` (add the project reference)

**Interfaces:**
- Consumes: `Envelope` and `RoomCode` from Task 1.
- Produces:
  - `readonly record struct Reply(IPEndPoint To, byte[] Data)`
  - `sealed class RoomRegistry(Func<DateTime> clock, Random random)`
  - `const int MaxRooms = 256`, `const int MaxMembers = 8`
  - `static readonly TimeSpan Forgotten = TimeSpan.FromSeconds(30)`
  - `IReadOnlyList<Reply> Heard(IPEndPoint from, ReadOnlySpan<byte> data)`
  - `void Sweep()`
  - `int RoomCount { get; }`
  - `bool TryFindByCode(string code, out Guid roomId)`

- [ ] **Step 1: Create the server project**

Create `GT2Relay/GT2Relay.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

    <PropertyGroup>
        <TargetFramework>net10.0</TargetFramework>
        <ImplicitUsings>enable</ImplicitUsings>
        <Nullable>enable</Nullable>
        <InvariantGlobalization>true</InvariantGlobalization>
    </PropertyGroup>

    <ItemGroup>
        <ProjectReference Include="../GT2Port.Rendezvous/GT2Port.Rendezvous.csproj" />
    </ItemGroup>

</Project>
```

A library for now. It becomes the program in Task 3, which is where `Program.cs`
arrives - an `Exe` with no entry point does not compile, so the two have to
land together.

Add to `tests/GT2Relay.Tests/GT2Relay.Tests.csproj`, inside the existing `ItemGroup` that holds the `ProjectReference`:

```xml
        <ProjectReference Include="../../GT2Relay/GT2Relay.csproj" />
```

- [ ] **Step 2: Write the failing tests**

Create `tests/GT2Relay.Tests/RoomRegistryTests.cs`:

```csharp
using System.Net;
using GT2Port.Rendezvous;
using Xunit;

namespace GT2Relay.Tests;

/// <summary>
/// Everything the server decides.
///
/// The clock is injected so expiry is a line of test rather than half a minute
/// of waiting, and the Random is seeded so a code is something a test can name.
/// Nothing here opens a socket: the loop that does is Task 3, and it should
/// have no decisions left in it.
/// </summary>
public class RoomRegistryTests
{
    DateTime _now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);
    void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    RoomRegistry New() => new(() => _now, new Random(1234));

    static readonly Guid Room = Guid.Parse("11111111-2222-3333-4444-555555555555");
    static readonly IPEndPoint Host = new(IPAddress.Parse("198.51.100.10"), 40000);
    static readonly IPEndPoint Guest = new(IPAddress.Parse("203.0.113.20"), 50000);
    static readonly IPEndPoint Third = new(IPAddress.Parse("203.0.113.21"), 50001);

    static byte[] Card(params byte[] bytes) => bytes;

    static List<Reply> Of(IReadOnlyList<Reply> replies, Envelope.Kind kind) =>
        [.. replies.Where(r => Envelope.TryReadKind(r.Data, out var k) && k == kind)];

    string Publish(RoomRegistry registry, Guid room, bool listed = true, IPEndPoint? host = null)
    {
        var replies = registry.Heard(host ?? Host,
            Envelope.WritePublish(room, listed, Card(1, 2, 3)));
        var published = Of(replies, Envelope.Kind.Published);
        Assert.Single(published);
        Assert.True(Envelope.TryReadPublished(published[0].Data, out _, out string code));
        return code;
    }

    [Fact]
    public void Publishing_a_room_answers_with_a_code()
    {
        var registry = New();

        string code = Publish(registry, Room);

        Assert.True(RoomCode.IsWellFormed(code));
        Assert.Equal(1, registry.RoomCount);
        Assert.True(registry.TryFindByCode(code, out var found));
        Assert.Equal(Room, found);
    }

    /// <summary>
    /// The host publishes on a timer, and every publish is also the keepalive
    /// that says the room is still there. A second publish must not mint a
    /// second code - the one on the host's screen has to keep working.
    /// </summary>
    [Fact]
    public void Publishing_again_keeps_the_same_code()
    {
        var registry = New();

        string first = Publish(registry, Room);
        Advance(5);
        string again = Publish(registry, Room);

        Assert.Equal(first, again);
        Assert.Equal(1, registry.RoomCount);
    }

    [Fact]
    public void A_listed_room_is_handed_out_and_an_unlisted_one_is_not()
    {
        var registry = New();
        Publish(registry, Room, listed: true);
        var quiet = Guid.NewGuid();
        Publish(registry, quiet, listed: false, host: Third);

        var cards = Of(registry.Heard(Guest, Envelope.WriteList()), Envelope.Kind.RoomCard);

        Assert.Single(cards);
        Assert.True(Envelope.TryReadRoomCard(cards[0].Data, out var id, out _, out var card));
        Assert.Equal(Room, id);
        Assert.Equal(Card(1, 2, 3), card);
    }

    /// <summary>
    /// A room nobody listed is still a room somebody was told the code of.
    /// </summary>
    [Fact]
    public void An_unlisted_room_can_still_be_joined_by_code()
    {
        var registry = New();
        string code = Publish(registry, Room, listed: false);

        var replies = registry.Heard(Guest, Envelope.WriteJoinByCode(code));

        Assert.Single(Of(replies, Envelope.Kind.Joined));
    }

    /// <summary>
    /// Joining is where the two sides learn each other's public address. Both
    /// have to be told, and each has to be told about the other rather than
    /// about itself - which is the mistake that makes a room where everyone
    /// talks to themselves.
    /// </summary>
    [Fact]
    public void Joining_tells_each_side_where_the_other_is()
    {
        var registry = New();
        Publish(registry, Room);

        var replies = registry.Heard(Guest, Envelope.WriteJoin(Room));

        var joined = Of(replies, Envelope.Kind.Joined);
        Assert.Single(joined);
        Assert.Equal(Guest, joined[0].To);

        var peers = Of(replies, Envelope.Kind.Peer);
        Assert.Equal(2, peers.Count);

        var toGuest = peers.Single(p => p.To.Equals(Guest));
        Assert.True(Envelope.TryReadPeer(toGuest.Data, out _, out var guestWasTold));
        Assert.Equal(Host, guestWasTold);

        var toHost = peers.Single(p => p.To.Equals(Host));
        Assert.True(Envelope.TryReadPeer(toHost.Data, out _, out var hostWasTold));
        Assert.Equal(Guest, hostWasTold);
    }

    [Fact]
    public void Joining_a_room_that_is_not_there_says_so()
    {
        var registry = New();

        var replies = registry.Heard(Guest, Envelope.WriteJoin(Guid.NewGuid()));

        Assert.Single(Of(replies, Envelope.Kind.NoRoom));
    }

    [Fact]
    public void A_code_that_names_nothing_says_so()
    {
        var registry = New();

        var replies = registry.Heard(Guest, Envelope.WriteJoinByCode("ZZZZZZ"));

        Assert.Single(Of(replies, Envelope.Kind.NoRoom));
    }

    [Fact]
    public void Relaying_carries_the_payload_to_the_other_side_saying_who_sent_it()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        byte[] payload = [0xA5, 1, 2, 3];
        var replies = registry.Heard(Guest, Envelope.WriteRelay(Room, Host, payload));

        var relayed = Of(replies, Envelope.Kind.Relayed);
        Assert.Single(relayed);
        Assert.Equal(Host, relayed[0].To);
        Assert.True(Envelope.TryReadRelayed(relayed[0].Data, out var room, out var from, out var got));
        Assert.Equal(Room, room);
        Assert.Equal(Guest, from);
        Assert.Equal(payload, got);
    }

    /// <summary>
    /// Otherwise the server is an open reflector: anybody could aim a payload
    /// at any address on the internet and have it arrive from here.
    /// </summary>
    [Fact]
    public void A_stranger_cannot_relay_through_a_room_it_never_joined()
    {
        var registry = New();
        Publish(registry, Room);

        var replies = registry.Heard(Third, Envelope.WriteRelay(Room, Host, [1, 2, 3]));

        Assert.Empty(replies);
    }

    [Fact]
    public void And_a_member_cannot_relay_to_an_address_outside_its_room()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        var outside = new IPEndPoint(IPAddress.Parse("192.0.2.99"), 9999);
        var replies = registry.Heard(Guest, Envelope.WriteRelay(Room, outside, [1, 2, 3]));

        Assert.Empty(replies);
    }

    [Fact]
    public void Leaving_takes_a_member_out_of_the_room()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        registry.Heard(Guest, Envelope.WriteLeave(Room));

        Assert.Empty(registry.Heard(Guest, Envelope.WriteRelay(Room, Host, [1])));
    }

    [Fact]
    public void A_room_whose_host_went_quiet_is_forgotten()
    {
        var registry = New();
        Publish(registry, Room);

        Advance(RoomRegistry.Forgotten.TotalSeconds + 1);
        registry.Sweep();

        Assert.Equal(0, registry.RoomCount);
        Assert.Single(Of(registry.Heard(Guest, Envelope.WriteJoin(Room)), Envelope.Kind.NoRoom));
    }

    [Fact]
    public void A_room_still_publishing_is_kept()
    {
        var registry = New();
        Publish(registry, Room);

        for (int i = 0; i < 10; i++)
        {
            Advance(RoomRegistry.Forgotten.TotalSeconds / 2);
            Publish(registry, Room);
            registry.Sweep();
        }

        Assert.Equal(1, registry.RoomCount);
    }

    /// <summary>
    /// A player whose machine was closed leaves nothing behind to say so, and
    /// a room that keeps a ghost seat is a room that fills up and stops
    /// letting anybody in.
    /// </summary>
    [Fact]
    public void A_member_that_went_quiet_is_dropped_but_the_room_stays()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        for (int i = 0; i < 4; i++)
        {
            Advance(RoomRegistry.Forgotten.TotalSeconds / 3);
            Publish(registry, Room);
            registry.Sweep();
        }

        Assert.Equal(1, registry.RoomCount);
        Assert.Empty(registry.Heard(Guest, Envelope.WriteRelay(Room, Host, [1])));
    }

    [Fact]
    public void Relaying_is_itself_a_sign_of_life()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        for (int i = 0; i < 4; i++)
        {
            Advance(RoomRegistry.Forgotten.TotalSeconds / 3);
            Publish(registry, Room);
            registry.Heard(Guest, Envelope.WriteRelay(Room, Host, [1]));
            registry.Sweep();
        }

        Assert.Single(Of(registry.Heard(Guest, Envelope.WriteRelay(Room, Host, [1])),
            Envelope.Kind.Relayed));
    }

    [Fact]
    public void A_full_room_refuses_the_next_arrival()
    {
        var registry = New();
        Publish(registry, Room);

        for (int i = 0; i < RoomRegistry.MaxMembers - 1; i++)
        {
            var member = new IPEndPoint(IPAddress.Parse("203.0.113.100"), 40000 + i);
            Assert.Single(Of(registry.Heard(member, Envelope.WriteJoin(Room)),
                Envelope.Kind.Joined));
        }

        var late = new IPEndPoint(IPAddress.Parse("203.0.113.200"), 41000);
        Assert.Single(Of(registry.Heard(late, Envelope.WriteJoin(Room)), Envelope.Kind.NoRoom));
    }

    [Fact]
    public void A_full_server_refuses_a_new_room_and_keeps_the_ones_it_has()
    {
        var registry = New();
        for (int i = 0; i < RoomRegistry.MaxRooms; i++)
        {
            var host = new IPEndPoint(IPAddress.Parse("198.51.100.10"), 40000 + i);
            Publish(registry, Guid.NewGuid(), host: host);
        }

        var replies = registry.Heard(
            new IPEndPoint(IPAddress.Parse("198.51.100.99"), 45000),
            Envelope.WritePublish(Guid.NewGuid(), true, Card(1)));

        Assert.Single(Of(replies, Envelope.Kind.NoRoom));
        Assert.Equal(RoomRegistry.MaxRooms, registry.RoomCount);
    }

    [Fact]
    public void Nonsense_is_ignored_rather_than_answered()
    {
        var registry = New();

        Assert.Empty(registry.Heard(Guest, [1, 2, 3]));
        Assert.Empty(registry.Heard(Guest, []));
        Assert.Empty(registry.Heard(Guest, [Envelope.Magic, 99, 1]));
    }

    /// <summary>
    /// A host that reconnects comes from a new port, because its NAT gave it a
    /// new mapping. The room is the same room and its members should still be
    /// able to reach it.
    /// </summary>
    [Fact]
    public void A_host_that_comes_back_on_a_new_port_takes_the_room_with_it()
    {
        var registry = New();
        Publish(registry, Room);
        registry.Heard(Guest, Envelope.WriteJoin(Room));

        var moved = new IPEndPoint(Host.Address, Host.Port + 1);
        Publish(registry, Room, host: moved);

        var replies = registry.Heard(Guest, Envelope.WriteRelay(Room, moved, [1]));
        var relayed = Of(replies, Envelope.Kind.Relayed);
        Assert.Single(relayed);
        Assert.Equal(moved, relayed[0].To);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test tests/GT2Relay.Tests -c Debug --filter RoomRegistryTests`
Expected: FAIL — `RoomRegistry` does not exist.

- [ ] **Step 4: Write `RoomRegistry`**

Create `GT2Relay/RoomRegistry.cs`:

```csharp
using System.Net;
using GT2Port.Rendezvous;

namespace GT2Relay;

/// <summary>Something to send, and where.</summary>
public readonly record struct Reply(IPEndPoint To, byte[] Data);

/// <summary>
/// Every decision the relay makes.
///
/// Separated from the socket on purpose. A server's hard parts are expiry,
/// caps and who is allowed to talk to whom, and none of those are easier to
/// think about with a network in the way: here the clock is a function and
/// thirty seconds is a line of test.
///
/// It knows nothing about GT2. A room's description is a blob it was handed
/// and hands back, and relayed traffic is a payload it copies from one
/// datagram into another. That is what lets the game's own protocol change
/// without this being redeployed.
/// </summary>
public sealed class RoomRegistry(Func<DateTime> clock, Random random)
{
    /// <summary>
    /// Caps, because this listens on the open internet. None of them is a
    /// guess about demand - they are the point past which a stranger is
    /// costing somebody else a game.
    /// </summary>
    public const int MaxRooms = 256;
    public const int MaxMembers = 8;

    /// <summary>
    /// How long silence lasts before it means gone. A host publishes every two
    /// seconds and a racing client sends constantly, so thirty seconds is many
    /// missed messages rather than one unlucky one.
    /// </summary>
    public static readonly TimeSpan Forgotten = TimeSpan.FromSeconds(30);

    sealed class Room
    {
        public required Guid Id;
        public required string Code;
        public required IPEndPoint Host;
        public bool Listed;
        public byte[] Card = [];
        public DateTime HeardFromHost;
        public readonly Dictionary<IPEndPoint, DateTime> Members = [];
    }

    readonly Dictionary<Guid, Room> _rooms = [];
    readonly Dictionary<string, Guid> _codes = new(StringComparer.Ordinal);

    public int RoomCount => _rooms.Count;

    public bool TryFindByCode(string code, out Guid roomId) =>
        _codes.TryGetValue(code, out roomId);

    /// <summary>
    /// What to do about one datagram. Returns what to send; an empty list is
    /// the normal answer to anything malformed, unauthorised or unknown -
    /// answering a stranger is how a server becomes something to point at
    /// somebody else.
    /// </summary>
    public IReadOnlyList<Reply> Heard(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!Envelope.TryReadKind(data, out var kind)) return [];

        return kind switch
        {
            Envelope.Kind.Publish => Publish(from, data),
            Envelope.Kind.List => List(from),
            Envelope.Kind.Join => Join(from, data),
            Envelope.Kind.JoinByCode => JoinByCode(from, data),
            Envelope.Kind.Relay => Relay(from, data),
            Envelope.Kind.Leave => Leave(from, data),
            _ => [],
        };
    }

    IReadOnlyList<Reply> Publish(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!Envelope.TryReadPublish(data, out var roomId, out bool listed, out var card))
            return [];

        var now = clock();

        if (!_rooms.TryGetValue(roomId, out var room))
        {
            if (_rooms.Count >= MaxRooms) return [new Reply(from, Envelope.WriteNoRoom())];

            room = new Room { Id = roomId, Code = MintCode(), Host = from };
            _rooms[roomId] = room;
            _codes[room.Code] = roomId;
        }

        // The host may come back on a different port - its NAT hands out a new
        // mapping every time the socket is rebuilt - and when it does, the
        // room moves with it rather than becoming unreachable.
        room.Host = from;
        room.Listed = listed;
        room.Card = card;
        room.HeardFromHost = now;
        room.Members[from] = now;

        return [new Reply(from, Envelope.WritePublished(roomId, room.Code))];
    }

    string MintCode()
    {
        // A collision is one room in a thousand million, and the loop is what
        // makes that number irrelevant rather than something to reason about.
        for (int i = 0; i < 32; i++)
        {
            string code = RoomCode.Next(random);
            if (!_codes.ContainsKey(code)) return code;
        }
        return RoomCode.Next(random);
    }

    IReadOnlyList<Reply> List(IPEndPoint from)
    {
        var replies = new List<Reply>();
        foreach (var room in _rooms.Values)
        {
            if (!room.Listed) continue;
            replies.Add(new Reply(from, Envelope.WriteRoomCard(room.Id, room.Code, room.Card)));
        }
        return replies;
    }

    IReadOnlyList<Reply> Join(IPEndPoint from, ReadOnlySpan<byte> data) =>
        Envelope.TryReadJoin(data, out var roomId)
            ? Admit(from, roomId)
            : [];

    IReadOnlyList<Reply> JoinByCode(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!Envelope.TryReadJoinByCode(data, out string code)) return [];
        if (!_codes.TryGetValue(code, out var roomId))
            return [new Reply(from, Envelope.WriteNoRoom())];
        return Admit(from, roomId);
    }

    IReadOnlyList<Reply> Admit(IPEndPoint from, Guid roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var room))
            return [new Reply(from, Envelope.WriteNoRoom())];

        if (!room.Members.ContainsKey(from) && room.Members.Count >= MaxMembers)
            return [new Reply(from, Envelope.WriteNoRoom())];

        room.Members[from] = clock();

        var replies = new List<Reply>
        {
            new(from, Envelope.WriteJoined(roomId)),
            new(from, Envelope.WritePeer(roomId, room.Host)),
        };

        // The host is told too, and this is not a courtesy: the host's own
        // link only sends to endpoints it knows, so without this the answer to
        // the first knock would have nowhere to go.
        if (!from.Equals(room.Host))
            replies.Add(new Reply(room.Host, Envelope.WritePeer(roomId, from)));

        return replies;
    }

    IReadOnlyList<Reply> Relay(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!Envelope.TryReadRelay(data, out var roomId, out var to, out var payload)) return [];
        if (!_rooms.TryGetValue(roomId, out var room)) return [];

        // Both ends have to be in the room. Without the first check this would
        // forward for anybody; without the second it would forward to anybody,
        // and either one is a machine on the internet that sends traffic
        // wherever a stranger points it.
        if (!room.Members.ContainsKey(from)) return [];
        if (!room.Members.ContainsKey(to)) return [];

        room.Members[from] = clock();

        return [new Reply(to, Envelope.WriteRelayed(roomId, from, payload))];
    }

    IReadOnlyList<Reply> Leave(IPEndPoint from, ReadOnlySpan<byte> data)
    {
        if (!Envelope.TryReadLeave(data, out var roomId)) return [];
        if (!_rooms.TryGetValue(roomId, out var room)) return [];

        room.Members.Remove(from);
        return [];
    }

    /// <summary>
    /// Forgets what has gone quiet. Called on a timer by whatever owns this;
    /// nothing expires on its own, so a test can advance a clock and ask.
    /// </summary>
    public void Sweep()
    {
        var now = clock();

        foreach (var roomId in _rooms.Keys.ToList())
        {
            var room = _rooms[roomId];

            if (now - room.HeardFromHost > Forgotten)
            {
                _rooms.Remove(roomId);
                _codes.Remove(room.Code);
                continue;
            }

            foreach (var member in room.Members.Keys.ToList())
            {
                if (member.Equals(room.Host)) continue;
                if (now - room.Members[member] > Forgotten) room.Members.Remove(member);
            }
        }
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/GT2Relay.Tests -c Debug`
Expected: PASS, 49 tests - `EnvelopeTests`, `RoomCodeTests` and `RoomRegistryTests`.

- [ ] **Step 6: Commit**

```bash
git add GT2Relay tests/GT2Relay.Tests
git commit -m "Teach a registry who is in which room, and for how long"
```

---

## Task 3: The server itself

The socket loop, and nothing else — every decision was made in Task 2. This task ends with a program that can be copied to the Oracle box and run.

**Files:**
- Create: `GT2Relay/RelayServer.cs`
- Create: `GT2Relay/Program.cs`
- Create: `GT2Relay/README.md`
- Test: `tests/GT2Relay.Tests/RelayServerTests.cs`

**Interfaces:**
- Consumes: `RoomRegistry`, `Reply`, `Envelope` from Tasks 1 and 2.
- Produces:
  - `sealed class RelayServer : IDisposable`
  - `RelayServer(int port, Func<DateTime>? clock = null, Random? random = null)`
  - `int BoundPort { get; }`
  - `int Pump()` — answers every datagram already waiting; returns how many it handled
  - `void Sweep()`
  - `void Run(CancellationToken stopping)`

- [ ] **Step 1: Write the failing test**

Create `tests/GT2Relay.Tests/RelayServerTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using GT2Port.Rendezvous;
using Xunit;

namespace GT2Relay.Tests;

/// <summary>
/// The registry's decisions, this time over a real socket.
///
/// Loopback rather than a mock: the thing being tested is precisely that
/// datagrams go out of the right socket to the right address, and a fake
/// socket would be a test of the fake. Port 0 so the OS picks, and the server
/// says which it got - two of these running at once must not collide.
/// </summary>
public class RelayServerTests
{
    /// <summary>
    /// A datagram put on the wire is not a datagram delivered. Loopback is
    /// fast but not instant, so this waits for the socket to say it has
    /// something rather than sleeping a guessed amount.
    /// </summary>
    static byte[] WaitFor(UdpClient socket, Func<byte[], bool> wanted)
    {
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until)
        {
            while (socket.Available > 0)
            {
                IPEndPoint? from = null;
                var data = socket.Receive(ref from);
                if (wanted(data)) return data;
            }
            Thread.Sleep(5);
        }
        Assert.Fail("nothing that was being waited for arrived within two seconds");
        return [];
    }

    static UdpClient Player()
    {
        var socket = new UdpClient(0);
        socket.Client.ReceiveTimeout = 500;
        return socket;
    }

    static void Send(UdpClient from, RelayServer server, byte[] data) =>
        from.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, server.BoundPort));

    static bool Kind(byte[] data, Envelope.Kind kind) =>
        Envelope.TryReadKind(data, out var got) && got == kind;

    [Fact]
    public void A_host_publishes_and_a_client_finds_the_room_and_they_talk()
    {
        using var server = new RelayServer(0);
        using var host = Player();
        using var guest = Player();

        var roomId = Guid.NewGuid();
        byte[] card = [42, 43, 44];

        Send(host, server, Envelope.WritePublish(roomId, listed: true, card));
        server.Pump();

        var published = WaitFor(host, d => Kind(d, Envelope.Kind.Published));
        Assert.True(Envelope.TryReadPublished(published, out _, out string code));
        Assert.True(RoomCode.IsWellFormed(code));

        Send(guest, server, Envelope.WriteList());
        server.Pump();

        var listed = WaitFor(guest, d => Kind(d, Envelope.Kind.RoomCard));
        Assert.True(Envelope.TryReadRoomCard(listed, out var listedId, out _, out var listedCard));
        Assert.Equal(roomId, listedId);
        Assert.Equal(card, listedCard);

        Send(guest, server, Envelope.WriteJoin(roomId));
        server.Pump();

        var peerForGuest = WaitFor(guest, d => Kind(d, Envelope.Kind.Peer));
        Assert.True(Envelope.TryReadPeer(peerForGuest, out _, out var hostEndPoint));

        var peerForHost = WaitFor(host, d => Kind(d, Envelope.Kind.Peer));
        Assert.True(Envelope.TryReadPeer(peerForHost, out _, out var guestEndPoint));

        // What the two sides now know about each other is what the relay saw,
        // which on loopback is the port each socket bound.
        Assert.Equal(((IPEndPoint)host.Client.LocalEndPoint!).Port, hostEndPoint.Port);
        Assert.Equal(((IPEndPoint)guest.Client.LocalEndPoint!).Port, guestEndPoint.Port);

        byte[] payload = [0xA5, 9, 8, 7];
        Send(guest, server, Envelope.WriteRelay(roomId, hostEndPoint, payload));
        server.Pump();

        var arrived = WaitFor(host, d => Kind(d, Envelope.Kind.Relayed));
        Assert.True(Envelope.TryReadRelayed(arrived, out _, out var from, out var got));
        Assert.Equal(guestEndPoint, from);
        Assert.Equal(payload, got);
    }

    [Fact]
    public void A_code_reaches_a_room_that_was_never_listed()
    {
        using var server = new RelayServer(0);
        using var host = Player();
        using var guest = Player();

        var roomId = Guid.NewGuid();
        Send(host, server, Envelope.WritePublish(roomId, listed: false, [1]));
        server.Pump();

        var published = WaitFor(host, d => Kind(d, Envelope.Kind.Published));
        Assert.True(Envelope.TryReadPublished(published, out _, out string code));

        Send(guest, server, Envelope.WriteList());
        server.Pump();
        Assert.Equal(0, guest.Available);

        Send(guest, server, Envelope.WriteJoinByCode(code));
        server.Pump();

        var joined = WaitFor(guest, d => Kind(d, Envelope.Kind.Joined));
        Assert.True(Envelope.TryReadJoined(joined, out var joinedId));
        Assert.Equal(roomId, joinedId);
    }

    /// <summary>
    /// A server that falls over on a malformed datagram is a server anybody
    /// can turn off with one packet.
    /// </summary>
    [Fact]
    public void Rubbish_does_not_stop_it_serving()
    {
        using var server = new RelayServer(0);
        using var noise = Player();
        using var host = Player();

        Send(noise, server, [0, 1, 2, 3, 4, 5]);
        Send(noise, server, []);
        Send(noise, server, [Envelope.Magic, 200, 200, 200]);
        server.Pump();

        Send(host, server, Envelope.WritePublish(Guid.NewGuid(), true, [1]));
        server.Pump();

        WaitFor(host, d => Kind(d, Envelope.Kind.Published));
    }

    [Fact]
    public void It_says_which_port_it_got()
    {
        using var server = new RelayServer(0);

        Assert.True(server.BoundPort > 0);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/GT2Relay.Tests -c Debug --filter RelayServerTests`
Expected: FAIL — `RelayServer` does not exist.

- [ ] **Step 3: Write `RelayServer`**

Create `GT2Relay/RelayServer.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using GT2Port.Rendezvous;

namespace GT2Relay;

/// <summary>
/// The socket around <see cref="RoomRegistry"/>.
///
/// Deliberately thin. Everything worth arguing about - who may talk to whom,
/// what expires, what the caps are - was decided in the registry, where it can
/// be tested without a network. What is left here is reading a datagram,
/// asking, and sending the answers.
///
/// One thread and one socket. A relay for a handful of six-player rooms moves
/// a few thousand small datagrams a second at most, which one thread handles
/// without noticing; threads would buy nothing and cost a lock around every
/// room.
/// </summary>
public sealed class RelayServer : IDisposable
{
    readonly UdpClient _socket;
    readonly RoomRegistry _registry;
    bool _disposed;

    /// <summary>How often to forget what has gone quiet.</summary>
    public static readonly TimeSpan SweepEvery = TimeSpan.FromSeconds(5);

    public RelayServer(int port, Func<DateTime>? clock = null, Random? random = null)
    {
        _socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));

        // On Windows a UDP socket that sends to a closed port gets an ICMP
        // "port unreachable" back, and the default is to surface that on the
        // *next* receive as a ConnectionReset - so one departed player would
        // otherwise break reads for everybody. There is no way to answer it
        // usefully, so it is turned off.
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            _socket.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
            // Not Windows. Nothing to turn off.
        }

        BoundPort = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
        _registry = new RoomRegistry(clock ?? (() => DateTime.UtcNow), random ?? new Random());
    }

    /// <summary>The port it actually got, which is not the one asked for when 0 was.</summary>
    public int BoundPort { get; }

    public int RoomCount => _registry.RoomCount;

    /// <summary>
    /// Answers everything already waiting, and returns how many datagrams that
    /// was. Never blocks: a test drives it a datagram at a time, and the loop
    /// in <see cref="Run"/> calls it when the socket says there is something.
    /// </summary>
    public int Pump()
    {
        if (_disposed) return 0;

        int handled = 0;
        while (_socket.Available > 0)
        {
            IPEndPoint? from = null;
            byte[] data;
            try
            {
                data = _socket.Receive(ref from);
            }
            catch (SocketException)
            {
                // One unhappy datagram is not a reason to stop serving
                // everybody else.
                break;
            }

            handled++;
            if (from is null) continue;

            foreach (var reply in _registry.Heard(from, data))
            {
                try
                {
                    _socket.Send(reply.Data, reply.Data.Length, reply.To);
                }
                catch (SocketException)
                {
                    // The peer went away between asking and being answered.
                    // It will expire on its own.
                }
            }
        }
        return handled;
    }

    public void Sweep() => _registry.Sweep();

    /// <summary>
    /// Serves until asked to stop. Blocks on the socket rather than spinning,
    /// with a timeout only so the sweep still happens on a quiet night.
    /// </summary>
    public void Run(CancellationToken stopping)
    {
        var nextSweep = DateTime.UtcNow + SweepEvery;
        _socket.Client.ReceiveTimeout = 500;

        while (!stopping.IsCancellationRequested)
        {
            if (_socket.Available > 0) Pump();
            else Thread.Sleep(1);

            if (DateTime.UtcNow >= nextSweep)
            {
                Sweep();
                nextSweep = DateTime.UtcNow + SweepEvery;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket.Dispose();
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/GT2Relay.Tests -c Debug`
Expected: PASS — all four suites.

- [ ] **Step 5: Write `Program`**

First make the project a program. In `GT2Relay/GT2Relay.csproj`, add to the
`PropertyGroup`:

```xml
        <OutputType>Exe</OutputType>
        <AssemblyName>gt2relay</AssemblyName>
```

Then create `GT2Relay/Program.cs`:

```csharp
using GT2Relay;

int port = 34720;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--port" or "-p")
    {
        if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out port) || port is < 1 or > 65535)
        {
            Console.Error.WriteLine("--port wants a number between 1 and 65535");
            return 1;
        }
        i++;
    }
    else
    {
        Console.Error.WriteLine($"gt2relay [--port {port}]");
        return 1;
    }
}

using var server = new RelayServer(port);
using var stopping = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopping.Cancel();
};

Console.WriteLine($"gt2relay listening on udp/{server.BoundPort}");
Console.WriteLine("rooms are forgotten after "
    + $"{RoomRegistry.Forgotten.TotalSeconds:F0}s without a publish");

server.Run(stopping.Token);

Console.WriteLine("stopped");
return 0;
```

- [ ] **Step 6: Run it once by hand**

Run: `dotnet run --project GT2Relay -c Release -- --port 34720`
Expected: prints `gt2relay listening on udp/34720` and stays up until `Ctrl+C`, which prints `stopped`.

- [ ] **Step 7: Write the deployment notes**

Create `GT2Relay/README.md`:

````markdown
# gt2relay

A rendezvous and relay for GT2 rooms. It exists so that neither the host nor
the players have to forward a port: both reach this with outbound UDP, which
every home router allows, and it passes their traffic along.

It understands nothing about GT2. A room's description is a blob it stores and
hands back; a room's traffic is a payload it copies from one datagram to
another. The game's protocol can change without this being redeployed.

## Running it

```bash
dotnet publish GT2Relay -c Release -r linux-x64 --self-contained false -o out
./out/gt2relay --port 34720
```

## On Oracle Cloud free tier

Pick the **`VM.Standard.A1.Flex`** shape — up to 4 OCPU and 24 GB are Always
Free, against 1 OCPU and 1 GB on the AMD micro. Reserve a public IP rather than
keeping the ephemeral one: an ephemeral address changes when the instance is
stopped, and the address is what players type.

**There are two firewalls and both block UDP by default.** Opening one and not
the other looks exactly like the server being down.

1. In the console: *Networking → Virtual Cloud Networks → your VCN → Security
   Lists → Default Security List → Add Ingress Rule*. Source `0.0.0.0/0`,
   IP Protocol **UDP**, destination port range `34720`.

2. On the instance, where Oracle's images ship a populated `iptables` that
   `ufw` does not front:

```bash
sudo iptables -I INPUT -p udp --dport 34720 -j ACCEPT
sudo netfilter-persistent save
```

### As a service

`/etc/systemd/system/gt2relay.service`:

```ini
[Unit]
Description=GT2 rendezvous and relay
After=network-online.target

[Service]
ExecStart=/opt/gt2relay/gt2relay --port 34720
Restart=always
RestartSec=2
User=gt2relay
DynamicUser=yes
NoNewPrivileges=yes
PrivateTmp=yes
ProtectSystem=strict
ProtectHome=yes

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl enable --now gt2relay
sudo systemctl status gt2relay
```

## Checking it from somewhere else

```bash
sudo tcpdump -n -i any udp port 34720
```

Traffic arriving here but nothing going back means the outbound path is
blocked; nothing arriving at all means one of the two firewalls above.

## What it costs

Six players at 60 datagrams a second, ~29 bytes of payload each, is about
100 KB/s per room in and the same out. Oracle's Always Free allowance is 10 TB
of egress a month, which is thousands of room-hours.
````

- [ ] **Step 8: Commit**

```bash
git add GT2Relay tests/GT2Relay.Tests
git commit -m "Put a socket around the registry, and notes for the box it runs on"
```

---

## Task 4: A seam under LanSession

`LanSession` holds a `UdpClient` and uses exactly four things from it. Putting an interface there is what lets the relay exist at all — and this task must change no behaviour, so the 401 existing tests are the check.

**Files:**
- Create: `patches/multiplayer/IGameLink.cs`
- Create: `patches/multiplayer/DirectLink.cs`
- Modify: `patches/multiplayer/LanSession.cs`
- Test: `tests/GT2Port.Tests/GameLinkTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `interface IGameLink : IDisposable` with `int Available { get; }`, `byte[] Receive(ref IPEndPoint? from)`, `void Send(byte[] data, int length, IPEndPoint to)`, `int BoundPort { get; }`
  - `sealed class DirectLink : IGameLink` with `static DirectLink Bind(int port)` and `static DirectLink Ephemeral()`
  - `LanSession.Over(IGameLink link, int hostPort, Func<DateTime> clock, bool hosting)`

- [ ] **Step 1: Write the failing test**

Create `tests/GT2Port.Tests/GameLinkTests.cs`:

```csharp
using System.Net;
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// The seam the relay needs.
///
/// LanSession used to hold a UdpClient. It uses four things from one - how
/// much is waiting, take the next, send this, and let go - and those four are
/// the whole of this interface. Everything above it (rooms, intents, places,
/// the start barrier) is unchanged and must stay that way: the existing suite
/// is the proof, and these only pin the seam itself.
/// </summary>
public class GameLinkTests
{
    // Clear of every other port this suite binds - see LanSessionTests for why
    // each file needs its own.
    const int BasePort = 34830;

    [Fact]
    public void A_direct_link_binds_the_port_it_was_asked_for()
    {
        using var link = DirectLink.Bind(BasePort);

        Assert.Equal(BasePort, link.BoundPort);
    }

    [Fact]
    public void An_ephemeral_link_gets_a_port_of_its_own()
    {
        using var one = DirectLink.Ephemeral();
        using var two = DirectLink.Ephemeral();

        Assert.True(one.BoundPort > 0);
        Assert.NotEqual(one.BoundPort, two.BoundPort);
    }

    [Fact]
    public void What_goes_in_one_link_comes_out_of_the_other_with_the_sender_named()
    {
        using var listener = DirectLink.Bind(BasePort + 1);
        using var sender = DirectLink.Ephemeral();

        byte[] data = [1, 2, 3, 4];
        sender.Send(data, data.Length, new IPEndPoint(IPAddress.Loopback, listener.BoundPort));

        var until = DateTime.UtcNow.AddSeconds(2);
        while (listener.Available == 0 && DateTime.UtcNow < until) Thread.Sleep(5);
        Assert.True(listener.Available > 0, "the datagram never arrived");

        IPEndPoint? from = null;
        var got = listener.Receive(ref from);

        Assert.Equal(data, got);
        Assert.NotNull(from);
        Assert.Equal(sender.BoundPort, from!.Port);
    }

    /// <summary>
    /// A session built over a link behaves as one built over a socket - which
    /// is the whole claim of this task.
    /// </summary>
    [Fact]
    public void A_session_can_be_built_over_a_link()
    {
        using var link = DirectLink.Bind(BasePort + 2);
        using var session = LanSession.Over(link, link.BoundPort, () => DateTime.UtcNow,
            hosting: true);

        Assert.Equal(BasePort + 2, session.BoundPort);
    }

    [Fact]
    public void Letting_go_of_a_session_lets_go_of_its_link()
    {
        var link = DirectLink.Bind(BasePort + 3);
        var session = LanSession.Over(link, link.BoundPort, () => DateTime.UtcNow, hosting: true);

        session.Dispose();

        // The port is free again, which it would not be if the link were still
        // holding it.
        using var again = DirectLink.Bind(BasePort + 3);
        Assert.Equal(BasePort + 3, again.BoundPort);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/GT2Port.Tests -c Debug --filter GameLinkTests`
Expected: FAIL — `DirectLink` and `LanSession.Over` do not exist.

- [ ] **Step 3: Write the interface**

Create `patches/multiplayer/IGameLink.cs`:

```csharp
using System.Net;

namespace GT2Port.Multiplayer;

/// <summary>
/// Where a session's datagrams go, and where they come from.
///
/// This is the four things <see cref="LanSession"/> ever asked a UdpClient
/// for, and no more. It exists because the relay needed a second answer to
/// "how do these bytes reach that player" - over the local network they are
/// simply addressed to it, and over the internet they are wrapped up and sent
/// to a server that passes them on. Everything above this seam - rooms,
/// intents, places, the start barrier - cannot tell the difference, which is
/// the point: none of it should have to learn about NAT.
///
/// <see cref="Receive"/> hands back who sent it, and that identity is what the
/// lobby keys players by. A relayed link therefore reports the *peer's* public
/// address rather than the relay's, or every player in a room would look like
/// the same one.
/// </summary>
public interface IGameLink : IDisposable
{
    /// <summary>How many datagrams are waiting.</summary>
    int Available { get; }

    /// <summary>
    /// Takes the next one. Throws <see cref="System.Net.Sockets.SocketException"/>
    /// the way a UdpClient does, because every caller already handles that.
    /// </summary>
    byte[] Receive(ref IPEndPoint? from);

    void Send(byte[] data, int length, IPEndPoint to);

    /// <summary>The port this machine is reachable on, as this machine sees it.</summary>
    int BoundPort { get; }
}
```

- [ ] **Step 4: Write the direct link**

Create `patches/multiplayer/DirectLink.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace GT2Port.Multiplayer;

/// <summary>
/// A link that is just a socket - what every session was before the relay
/// existed, and still what one is on a local network.
///
/// The binding rules are the ones <see cref="LanSession"/> had and the reasons
/// have not changed: a host takes the well-known port without
/// <see cref="SocketOptionName.ReuseAddress"/>, so a second host on the same
/// machine fails loudly instead of binding successfully and then silently
/// receiving nothing; a client takes port 0 so two clients on one machine
/// never collide.
/// </summary>
public sealed class DirectLink : IGameLink
{
    readonly UdpClient _socket;
    bool _disposed;

    DirectLink(UdpClient socket)
    {
        _socket = socket;
        BoundPort = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    public int BoundPort { get; }

    public static DirectLink Bind(int port) => Open(port);

    public static DirectLink Ephemeral() => Open(0);

    static DirectLink Open(int port)
    {
        var socket = new UdpClient
        {
            Client = { ReceiveTimeout = 1 },
        };
        try
        {
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch
        {
            // Leaving it undisposed leaks a handle per failed attempt - a
            // player clicking "Create a room" while another instance already
            // holds the port does exactly that, repeatedly.
            socket.Dispose();
            throw;
        }
        return new DirectLink(socket);
    }

    public int Available => _disposed ? 0 : _socket.Available;

    public byte[] Receive(ref IPEndPoint? from) => _socket.Receive(ref from);

    public void Send(byte[] data, int length, IPEndPoint to) => _socket.Send(data, length, to);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket.Dispose();
    }
}
```

- [ ] **Step 5: Retarget `LanSession` onto the seam**

In `patches/multiplayer/LanSession.cs`, make exactly these substitutions and no others:

Replace the field:

```csharp
    readonly UdpClient _socket;
```

with:

```csharp
    readonly IGameLink _link;
```

Replace the constructor signature and body:

```csharp
    LanSession(UdpClient socket, int boundPort, int hostPort, Func<DateTime> clock, bool hosting)
    {
        _socket = socket;
```

with:

```csharp
    LanSession(IGameLink link, int boundPort, int hostPort, Func<DateTime> clock, bool hosting)
    {
        _link = link;
```

Then replace every remaining use, of which there are exactly four shapes:

- `_socket.Available` → `_link.Available` (occurs in `Available` and in each drain's loop condition)
- `_socket.Receive(ref from)` → `_link.Receive(ref from)`
- `_socket.Send(data, data.Length, to)` → `_link.Send(data, data.Length, to)`
- `_socket.Dispose()` → `_link.Dispose()`

Verify none is left:

```bash
grep -n "_socket" patches/multiplayer/LanSession.cs
```

Expected: no output.

Replace the bodies of the two factories so they build a link. `ForHost` becomes:

```csharp
    public static LanSession ForHost(int port, Func<DateTime> clock)
    {
        var link = DirectLink.Bind(port);
        return new LanSession(link, link.BoundPort, link.BoundPort, clock, hosting: true);
    }
```

`ForClient` becomes:

```csharp
    public static LanSession ForClient(int hostPort, Func<DateTime> clock)
    {
        var link = DirectLink.Ephemeral();
        return new LanSession(link, link.BoundPort, hostPort, clock, hosting: false);
    }
```

Keep the existing XML doc comments above both — they explain the binding rules, which are now enforced inside `DirectLink` and are still the reason these two differ.

And add the third factory, next to them:

```csharp
    /// <summary>
    /// A session over a link somebody else built - which in practice means a
    /// relayed one. The two factories above are the local-network cases and
    /// build their own socket; this is the seam for everything that reaches a
    /// host some other way.
    /// </summary>
    public static LanSession Over(IGameLink link, int hostPort, Func<DateTime> clock, bool hosting) =>
        new(link, link.BoundPort, hostPort, clock, hosting);
```

If `using System.Net.Sockets;` is now unused in the file, leave it: `SocketException` is still caught in several drains.

- [ ] **Step 6: Run the new test**

Run: `dotnet test tests/GT2Port.Tests -c Debug --filter GameLinkTests`
Expected: PASS, 5 tests.

- [ ] **Step 7: Run everything, because this task's real claim is that nothing changed**

Run: `dotnet test tests/GT2Port.Tests -c Debug`
Expected: PASS - 5 more than before this task, and nothing that passed before now failing.

- [ ] **Step 8: Commit**

```bash
git add patches/multiplayer/IGameLink.cs patches/multiplayer/DirectLink.cs patches/multiplayer/LanSession.cs tests/GT2Port.Tests/GameLinkTests.cs
git commit -m "Put a seam where the session met the socket"
```

---

## Task 5: The relayed link

One socket that does both jobs — it asks the server about rooms *and* carries the game's traffic. One socket because that is one NAT mapping to keep alive: a second socket would have a second mapping that expires on its own schedule, and debugging why half a room works is not a thing to sign up for.

**Files:**
- Create: `patches/multiplayer/RelaySession.cs`
- Modify: `GT2Port.csproj` (reference `GT2Port.Rendezvous`)
- Modify: `tests/GT2Port.Tests/GT2Port.Tests.csproj` (reference `GT2Relay`, for a real server to talk to)
- Test: `tests/GT2Port.Tests/RelaySessionTests.cs`

**Interfaces:**
- Consumes: `IGameLink` from Task 4; `Envelope`, `RoomCode` from Task 1; `RelayServer` from Task 3 (tests only).
- Produces:
  - `sealed class RelaySession : IGameLink`
  - `RelaySession(IPEndPoint relay, Func<DateTime> clock)`
  - `readonly record struct Advert(Guid Id, string Code, byte[] Card)`
  - `void Publish(Guid roomId, bool listed, byte[] card)` — rate-limited to one every `PublishEvery`
  - `void AskForRooms()` — rate-limited to one every `ListEvery`
  - `void Join(Guid roomId)`, `void JoinByCode(string code)`, `void Leave()`
  - `IReadOnlyList<Advert> Rooms { get; }`
  - `IReadOnlyList<IPEndPoint> Peers { get; }`
  - `string Code { get; }`, `Guid RoomId { get; }`, `bool Admitted { get; }`, `bool Refused { get; }`
  - `static readonly TimeSpan PublishEvery`, `ListEvery`, `Forgotten`

- [ ] **Step 1: Wire the references**

In `GT2Port.csproj`, beside the existing `RecompOne.Runtime` reference:

```xml
        <ProjectReference Include="GT2Port.Rendezvous/GT2Port.Rendezvous.csproj" />
```

In `tests/GT2Port.Tests/GT2Port.Tests.csproj`, beside the existing `GT2Port` reference:

```xml
        <ProjectReference Include="../../GT2Relay/GT2Relay.csproj" />
```

The test project references the server so that these tests talk to the real one rather than to a hand-written stand-in. A stand-in would be a second implementation of the thing under test, and the first time the two disagreed the tests would pass and the game would not.

- [ ] **Step 2: Write the failing test**

Create `tests/GT2Port.Tests/RelaySessionTests.cs`:

```csharp
using System.Net;
using GT2Port.Multiplayer;
using GT2Port.Rendezvous;
using GT2Relay;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// A link that reaches a host through a server instead of directly.
///
/// Tested against the real relay on loopback. The claim being made is that
/// what a session sends comes out of the other session with the *peer* named
/// as the sender - not the relay - because that identity is what the lobby
/// keys players by, and a relay that hid it would make every player in a room
/// look like the same one.
/// </summary>
public class RelaySessionTests : IDisposable
{
    readonly RelayServer _server = new(0);
    DateTime _now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    IPEndPoint Where => new(IPAddress.Loopback, _server.BoundPort);
    RelaySession New() => new(Where, () => _now);
    void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    public void Dispose() => _server.Dispose();

    /// <summary>
    /// Pumps both sides for a while, because a datagram on loopback is fast
    /// but not instant and the server only answers when it is asked to.
    /// </summary>
    void Settle(params RelaySession[] sessions)
    {
        for (int i = 0; i < 40; i++)
        {
            _server.Pump();
            foreach (var session in sessions) _ = session.Available;
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Publishing_gets_a_code_back()
    {
        using var host = New();
        var roomId = Guid.NewGuid();

        host.Publish(roomId, listed: true, [1, 2, 3]);
        Settle(host);

        Assert.True(RoomCode.IsWellFormed(host.Code));
        Assert.Equal(roomId, host.RoomId);
    }

    [Fact]
    public void A_listed_room_turns_up_for_somebody_asking()
    {
        using var host = New();
        using var guest = New();
        var roomId = Guid.NewGuid();
        byte[] card = [9, 8, 7];

        host.Publish(roomId, listed: true, card);
        Settle(host);

        guest.AskForRooms();
        Settle(host, guest);

        var room = Assert.Single(guest.Rooms);
        Assert.Equal(roomId, room.Id);
        Assert.Equal(card, room.Card);
        Assert.Equal(host.Code, room.Code);
    }

    [Fact]
    public void An_unlisted_room_is_reached_by_its_code_and_not_by_the_list()
    {
        using var host = New();
        using var guest = New();
        var roomId = Guid.NewGuid();

        host.Publish(roomId, listed: false, [1]);
        Settle(host);

        guest.AskForRooms();
        Settle(host, guest);
        Assert.Empty(guest.Rooms);

        guest.JoinByCode(host.Code);
        Settle(host, guest);

        Assert.True(guest.Admitted);
        Assert.Equal(roomId, guest.RoomId);
    }

    [Fact]
    public void A_code_that_names_nothing_is_refused_rather_than_ignored()
    {
        using var guest = New();

        guest.JoinByCode("ZZZZZZ");
        Settle(guest);

        Assert.True(guest.Refused);
        Assert.False(guest.Admitted);
    }

    /// <summary>
    /// The whole point of the thing.
    /// </summary>
    [Fact]
    public void What_one_side_sends_arrives_at_the_other_with_the_peer_named()
    {
        using var host = New();
        using var guest = New();
        var roomId = Guid.NewGuid();

        host.Publish(roomId, listed: true, [1]);
        Settle(host);
        guest.Join(roomId);
        Settle(host, guest);

        var hostPeer = Assert.Single(guest.Peers);
        var guestPeer = Assert.Single(host.Peers);
        Assert.NotEqual(hostPeer, guestPeer);

        byte[] said = [0xA5, 1, 2, 3];
        guest.Send(said, said.Length, hostPeer);
        Settle(host, guest);

        Assert.True(host.Available > 0);
        IPEndPoint? from = null;
        var heard = host.Receive(ref from);

        Assert.Equal(said, heard);
        Assert.Equal(guestPeer, from);

        // And back the other way, addressed by what the host just learned.
        byte[] answered = [0xA5, 4, 5, 6];
        host.Send(answered, answered.Length, from!);
        Settle(host, guest);

        Assert.True(guest.Available > 0);
        IPEndPoint? backFrom = null;
        Assert.Equal(answered, guest.Receive(ref backFrom));
        Assert.Equal(hostPeer, backFrom);
    }

    /// <summary>
    /// Publishing is a keepalive on a timer, and a host that flooded the
    /// server with one publish per frame would be the first thing to get a
    /// relay rate-limited out of existence.
    /// </summary>
    [Fact]
    public void Publishing_more_often_than_the_interval_sends_nothing_extra()
    {
        using var host = New();
        var roomId = Guid.NewGuid();

        for (int i = 0; i < 50; i++) host.Publish(roomId, true, [1]);
        Settle(host);

        Assert.Equal(1, host.Published);

        Advance(RelaySession.PublishEvery.TotalSeconds + 0.1);
        host.Publish(roomId, true, [1]);
        Settle(host);

        Assert.Equal(2, host.Published);
    }

    /// <summary>
    /// A room whose host stopped publishing must leave the list, or the panel
    /// keeps offering a room nobody can join.
    /// </summary>
    [Fact]
    public void A_room_not_heard_of_lately_drops_out_of_the_list()
    {
        using var host = New();
        using var guest = New();

        host.Publish(Guid.NewGuid(), listed: true, [1]);
        Settle(host);
        guest.AskForRooms();
        Settle(host, guest);
        Assert.Single(guest.Rooms);

        Advance(RelaySession.Forgotten.TotalSeconds + 1);

        Assert.Empty(guest.Rooms);
    }

    [Fact]
    public void Rubbish_from_elsewhere_is_not_mistaken_for_game_traffic()
    {
        using var guest = New();
        using var stranger = new System.Net.Sockets.UdpClient(0);

        byte[] noise = [1, 2, 3, 4, 5];
        stranger.Send(noise, noise.Length,
            new IPEndPoint(IPAddress.Loopback, guest.BoundPort));

        Settle(guest);

        Assert.Equal(0, guest.Available);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test tests/GT2Port.Tests -c Debug --filter RelaySessionTests`
Expected: FAIL — `RelaySession` does not exist.

- [ ] **Step 4: Write `RelaySession`**

Create `patches/multiplayer/RelaySession.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using GT2Port.Rendezvous;

namespace GT2Port.Multiplayer;

/// <summary>
/// A link to a host nobody can address directly, through a server both sides
/// reach outbound.
///
/// This is one socket doing two jobs - asking the server about rooms, and
/// carrying the game's own traffic - and that is deliberate. Two sockets would
/// mean two NAT mappings, each expiring on its own schedule, and the failure
/// that produces is a room that lists fine and then goes quiet, which is a
/// miserable thing to debug.
///
/// It is an <see cref="IGameLink"/>, so <see cref="LanSession"/> and
/// everything above it works unchanged: <see cref="Receive"/> names the peer
/// that sent the payload rather than the relay that carried it, which is the
/// identity the lobby keys players by.
///
/// Nothing here understands the game's protocol either. A room's card is
/// whatever <see cref="RoomState.Serialise"/> produced and a payload is
/// whatever <see cref="LanSession"/> sent; both are bytes to be carried.
/// </summary>
public sealed class RelaySession : IGameLink
{
    /// <summary>
    /// How often a host says it is still there. Also the keepalive that holds
    /// the NAT mapping open - two seconds is well inside the shortest UDP
    /// timeout a home router is likely to use.
    /// </summary>
    public static readonly TimeSpan PublishEvery = TimeSpan.FromSeconds(2);

    /// <summary>How often a browser asks what rooms there are.</summary>
    public static readonly TimeSpan ListEvery = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a room stays on the list after it was last heard of. Longer
    /// than <see cref="ListEvery"/> by enough that one lost datagram does not
    /// make a room flicker out and back.
    /// </summary>
    public static readonly TimeSpan Forgotten = TimeSpan.FromSeconds(7);

    /// <summary>A room the server told us about.</summary>
    public readonly record struct Advert(Guid Id, string Code, byte[] Card);

    readonly UdpClient _socket;
    readonly IPEndPoint _relay;
    readonly Func<DateTime> _clock;

    readonly Queue<(byte[] Payload, IPEndPoint From)> _waiting = [];
    readonly Dictionary<Guid, (Advert Advert, DateTime Heard)> _rooms = [];
    readonly List<IPEndPoint> _peers = [];

    DateTime? _lastPublish;
    DateTime? _lastList;
    bool _disposed;

    public RelaySession(IPEndPoint relay, Func<DateTime> clock)
    {
        _relay = relay;
        _clock = clock;

        _socket = new UdpClient { Client = { ReceiveTimeout = 1 } };
        try
        {
            _socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        }
        catch
        {
            _socket.Dispose();
            throw;
        }

        // A relay that is not listening makes Windows deliver an ICMP refusal
        // as a ConnectionReset on the *next* receive, which would look like
        // the link breaking rather than like nobody answering.
        try
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            _socket.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
        }

        BoundPort = ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;
    }

    /// <summary>
    /// The local port. Not what peers address - that is the public endpoint
    /// the server saw, which arrives as a <see cref="Peers"/> entry on the
    /// other side.
    /// </summary>
    public int BoundPort { get; }

    public Guid RoomId { get; private set; }
    public string Code { get; private set; } = "";
    public bool Admitted { get; private set; }
    public bool Refused { get; private set; }

    /// <summary>How many publishes have actually gone out, for a test to count.</summary>
    public int Published { get; private set; }

    public IReadOnlyList<IPEndPoint> Peers => _peers;

    /// <summary>
    /// The rooms the server has mentioned lately. Kept with the time each was
    /// heard, so one that stops being mentioned falls off rather than sitting
    /// there being unjoinable.
    /// </summary>
    public IReadOnlyList<Advert> Rooms
    {
        get
        {
            var now = _clock();
            return [.. _rooms.Values.Where(r => now - r.Heard <= Forgotten).Select(r => r.Advert)];
        }
    }

    public void Publish(Guid roomId, bool listed, byte[] card)
    {
        if (_disposed) return;

        var now = _clock();
        if (_lastPublish is { } last && now - last < PublishEvery) return;
        _lastPublish = now;

        RoomId = roomId;
        Published++;
        Tell(Envelope.WritePublish(roomId, listed, card));
    }

    public void AskForRooms()
    {
        if (_disposed) return;

        var now = _clock();
        if (_lastList is { } last && now - last < ListEvery) return;
        _lastList = now;

        Tell(Envelope.WriteList());
    }

    public void Join(Guid roomId)
    {
        Refused = false;
        Tell(Envelope.WriteJoin(roomId));
    }

    public void JoinByCode(string code)
    {
        Refused = false;
        string tidy = RoomCode.Tidy(code);
        if (!RoomCode.IsWellFormed(tidy))
        {
            // Refused here rather than sent, so a typo is answered by this
            // machine instead of costing a round trip to say the same thing.
            Refused = true;
            return;
        }
        Tell(Envelope.WriteJoinByCode(tidy));
    }

    public void Leave()
    {
        if (RoomId == Guid.Empty) return;
        Tell(Envelope.WriteLeave(RoomId));
        Admitted = false;
    }

    void Tell(byte[] data)
    {
        if (_disposed) return;
        try { _socket.Send(data, data.Length, _relay); }
        catch (SocketException) { /* the next tick tries again */ }
    }

    // ---- IGameLink ----

    /// <summary>
    /// Drains the socket first, because everything that arrives here is
    /// wrapped and most of it is not game traffic at all - a count of what the
    /// socket holds would be a count of envelopes, not of payloads.
    /// </summary>
    public int Available
    {
        get
        {
            Pump();
            return _waiting.Count;
        }
    }

    public byte[] Receive(ref IPEndPoint? from)
    {
        Pump();
        if (_waiting.Count == 0) throw new SocketException((int)SocketError.WouldBlock);

        var (payload, sender) = _waiting.Dequeue();
        from = sender;
        return payload;
    }

    public void Send(byte[] data, int length, IPEndPoint to)
    {
        if (_disposed) return;
        if (length > Envelope.MaxPayload) return;

        var wrapped = Envelope.WriteRelay(RoomId, to, data.AsSpan(0, length));
        Tell(wrapped);
    }

    void Pump()
    {
        if (_disposed) return;

        while (_socket.Available > 0)
        {
            IPEndPoint? from = null;
            byte[] data;
            try { data = _socket.Receive(ref from); }
            catch (SocketException) { return; }

            // Only the relay is listened to. Anything else reaching this port
            // is a scanner or a stray, and treating it as game traffic would
            // put a stranger's bytes into the lobby.
            if (from is null || !from.Equals(_relay)) continue;
            if (!Envelope.TryReadKind(data, out var kind)) continue;

            switch (kind)
            {
                case Envelope.Kind.Published:
                    if (Envelope.TryReadPublished(data, out var published, out string code))
                    {
                        RoomId = published;
                        Code = code;
                    }
                    break;

                case Envelope.Kind.RoomCard:
                    if (Envelope.TryReadRoomCard(data, out var id, out string roomCode, out var card))
                        _rooms[id] = (new Advert(id, roomCode, card), _clock());
                    break;

                case Envelope.Kind.Joined:
                    if (Envelope.TryReadJoined(data, out var joined))
                    {
                        RoomId = joined;
                        Admitted = true;
                        Refused = false;
                    }
                    break;

                case Envelope.Kind.Peer:
                    if (Envelope.TryReadPeer(data, out _, out var peer) && !_peers.Contains(peer))
                        _peers.Add(peer);
                    break;

                case Envelope.Kind.Relayed:
                    if (Envelope.TryReadRelayed(data, out _, out var sender, out var payload))
                    {
                        // A peer that spoke is a peer worth knowing about, even
                        // if the server's introduction went missing.
                        if (!_peers.Contains(sender)) _peers.Add(sender);
                        _waiting.Enqueue((payload, sender));
                    }
                    break;

                case Envelope.Kind.NoRoom:
                    Refused = true;
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        Leave();
        _disposed = true;
        _socket.Dispose();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/GT2Port.Tests -c Debug --filter RelaySessionTests`
Expected: PASS, 8 tests.

- [ ] **Step 6: Run everything**

Run: `dotnet test tests/GT2Port.Tests -c Debug`
Expected: PASS - 8 more than before this task.

- [ ] **Step 7: Commit**

```bash
git add patches/multiplayer/RelaySession.cs GT2Port.csproj tests/GT2Port.Tests
git commit -m "Reach a host through a server both sides can call out to"
```

---

## Task 6: Wiring it into the game

The pieces exist and nothing uses them. This task puts a server address in the settings, a list of internet rooms beside the local ones, a code box, and the host's own code on the screen.

**Files:**
- Create: `patches/multiplayer/RelaySettings.cs`
- Modify: `patches/multiplayer/ModeHook.cs`
- Modify: `patches/multiplayer/MultiplayerPanel.cs`
- Test: `tests/GT2Port.Tests/RelaySettingsTests.cs`

**Interfaces:**
- Consumes: `RelaySession` from Task 5, `LanSession.Over` from Task 4.
- Produces:
  - `static class RelaySettings` with `static string Address { get; set; }`, `static bool Configured`, `static bool TryReadAddress(string typed, out IPEndPoint where)`, `const int DefaultPort = 34720`

- [ ] **Step 1: Write the failing test**

Create `tests/GT2Port.Tests/RelaySettingsTests.cs`:

```csharp
using GT2Port.Multiplayer;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// Where the relay lives, as a person types it.
///
/// A player is given an address by whoever runs the server, and pastes it into
/// a box. Most of them will paste it without a port, because a port is not a
/// thing most people think about - so the default has to be there.
/// </summary>
public class RelaySettingsTests
{
    [Theory]
    [InlineData("203.0.113.9", "203.0.113.9", RelaySettings.DefaultPort)]
    [InlineData("203.0.113.9:34720", "203.0.113.9", 34720)]
    [InlineData("203.0.113.9:40000", "203.0.113.9", 40000)]
    [InlineData("  203.0.113.9  ", "203.0.113.9", RelaySettings.DefaultPort)]
    public void An_address_with_or_without_a_port_is_read(string typed, string address, int port)
    {
        Assert.True(RelaySettings.TryReadAddress(typed, out var where));

        Assert.Equal(address, where.Address.ToString());
        Assert.Equal(port, where.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an address")]
    [InlineData("203.0.113.9:0")]
    [InlineData("203.0.113.9:70000")]
    [InlineData("203.0.113.9:abc")]
    public void And_something_that_is_not_one_is_refused(string typed)
    {
        Assert.False(RelaySettings.TryReadAddress(typed, out _));
    }

    /// <summary>
    /// A name rather than an address, because that is what somebody running a
    /// server will hand out once they have a domain.
    /// </summary>
    [Fact]
    public void A_name_that_resolves_is_read()
    {
        Assert.True(RelaySettings.TryReadAddress("localhost:34720", out var where));

        Assert.Equal(34720, where.Port);
    }

    [Fact]
    public void No_address_configured_means_the_relay_is_simply_off()
    {
        string was = RelaySettings.Address;
        try
        {
            RelaySettings.Address = "";
            Assert.False(RelaySettings.Configured);

            RelaySettings.Address = "203.0.113.9";
            Assert.True(RelaySettings.Configured);
        }
        finally
        {
            RelaySettings.Address = was;
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/GT2Port.Tests -c Debug --filter RelaySettingsTests`
Expected: FAIL — `RelaySettings` does not exist.

- [ ] **Step 3: Write `RelaySettings`**

Create `patches/multiplayer/RelaySettings.cs`:

```csharp
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
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/GT2Port.Tests -c Debug --filter RelaySettingsTests`
Expected: PASS, 12 tests.

- [ ] **Step 5: Build the relayed session in ModeHook**

In `patches/multiplayer/ModeHook.cs`, add a field beside `_lanSession`:

```csharp
    /// <summary>
    /// The socket to the rendezvous server, when one is configured. It is both
    /// how internet rooms are found and how their traffic travels, so it
    /// outlives any one session: dropping it between the room list and the
    /// lobby would drop the NAT mapping the whole thing depends on.
    /// </summary>
    static RelaySession? _relay;
```

Add a method that keeps it alive, and call it from the same per-frame place that already calls `_discovery.Tick()`:

```csharp
    /// <summary>
    /// Keeps the relay in step with what this machine is doing: a host says
    /// its room is still there, and anybody else asks what rooms exist. Both
    /// are rate-limited inside RelaySession, so calling this every frame is
    /// what it expects.
    /// </summary>
    static void TickTheRelay()
    {
        if (!RelaySettings.Configured)
        {
            _relay?.Dispose();
            _relay = null;
            return;
        }

        if (_relay is null)
        {
            if (!RelaySettings.TryReadAddress(RelaySettings.Address, out var where)) return;
            _relay = new RelaySession(where, () => DateTime.UtcNow);
            Console.Error.WriteLine($"[relay] talking to {where}");
        }

        if (_session.Phase == SessionPhase.Hosting && _session.Current is { } room)
            _relay.Publish(room.Id, listed: room.Secret.Length == 0, RoomState.Serialise(room));
        else
            _relay.AskForRooms();
    }
```

A room with a secret is not listed: the secret exists so a room can be private, and listing a private room defeats it while still refusing everybody at the door.

Then, where `SocketAction.RebuildAsClient` currently runs `_lanSession = LanSession.ForClient(SessionPort, () => DateTime.UtcNow);`, choose the relayed link when the room being reached came from the relay:

```csharp
                            _lanSession = _relay is { Admitted: true }
                                ? LanSession.Over(_relay, SessionPort, () => DateTime.UtcNow,
                                    hosting: false)
                                : LanSession.ForClient(SessionPort, () => DateTime.UtcNow);
```

and where `RebuildAsHost` runs `LanSession.ForHost(SessionPort, ...)`:

```csharp
                            _lanSession = RelaySettings.Configured && _relay is not null
                                ? LanSession.Over(_relay, SessionPort, () => DateTime.UtcNow,
                                    hosting: true)
                                : LanSession.ForHost(SessionPort, () => DateTime.UtcNow);
```

**The relay owns its socket and the session does not.** `LanSession.Dispose` calls `IGameLink.Dispose`, which would close the relay's socket underneath the room list. Guard it: in `patches/multiplayer/LanSession.cs`, `Over` takes a flag saying whether the session owns the link.

Change the `Over` factory added in Task 4 to:

```csharp
    public static LanSession Over(IGameLink link, int hostPort, Func<DateTime> clock,
                                  bool hosting, bool ownsTheLink = true) =>
        new(link, link.BoundPort, hostPort, clock, hosting) { _ownsTheLink = ownsTheLink };
```

which needs a field and a change to `Dispose`:

```csharp
    bool _ownsTheLink = true;
```

```csharp
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsTheLink) _link.Dispose();
    }
```

and both `ModeHook` call sites above pass `ownsTheLink: false`.

- [ ] **Step 6: Show it on the screen**

In `patches/multiplayer/MultiplayerPanel.cs`, in `DrawRoomList`, after the block that draws the LAN rooms and before the `ImGui.Separator()` that introduces "Or join by address", add the internet rooms:

```csharp
        // Rooms that were never on this network. Same row, same button - where
        // a room was found is not something a player should have to think
        // about, only whether they can get into it.
        if (_relay() is { } relay)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("Rooms on the internet");

            var adverts = relay.Rooms;
            if (adverts.Count == 0) ImGui.TextDisabled("None right now");

            foreach (var advert in adverts)
            {
                // A card that will not deserialise is a room published by a
                // newer build than this one. Skipping it is right: the panel
                // cannot draw a room it cannot read, and refusing to draw the
                // rest because of it would be worse.
                if (!RoomState.TryDeserialise(advert.Card, out var room)) continue;

                ImGui.PushID(advert.Id.ToString());
                var host = room.Players.Count > 0 ? room.Players[0].Name : "";
                var carClass = _carCatalogue.TryFind(room.CarGroup, out var carGroup)
                    ? carGroup.Name
                    : room.CarGroup;
                var label = $"{room.Name}   {host}   {room.Players.Count}/{room.MaxPlayers}"
                    + $"   {CourseTable.DisplayName(room.Track)}   {carClass}   {HowLong(room)}"
                    + $"   code {advert.Code}";

                bool full = room.Players.Count >= room.MaxPlayers;
                string buttonText = full ? "Full" : "Join";

                // Same truncation the LAN rows use, and for the same reason:
                // a room name at 150% display scale has no bound on this row's
                // width and the window has no horizontal scrollbar, so Join
                // has to keep its place.
                float buttonWidth = ImGui.CalcTextSize(buttonText).X
                    + ImGui.GetStyle().FramePadding.X * 2f;
                float available = ImGui.GetContentRegionAvail().X - buttonWidth
                    - ImGui.GetStyle().ItemSpacing.X;
                ImGui.TextUnformatted(Truncate(label, available));
                ImGui.SameLine();

                ImGui.BeginDisabled(full);
                if (ImGui.Button(buttonText)) JoinOverTheRelay(advert.Id, room);
                ImGui.EndDisabled();
                ImGui.PopID();
            }
        }
```

and after the "Join by address" button, a code box:

```csharp
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
```

with the field and the accessor beside the panel's other injected dependencies:

```csharp
    string _roomCodeTyped = "";
    readonly Func<RelaySession?> _relay;
```

The relay arrives the same way the session's socket does — as a function rather than an instance, because it is built and dropped as settings change and the panel outlives any one of them. Change the constructor at `patches/multiplayer/MultiplayerPanel.cs:62` from:

```csharp
    public MultiplayerPanel(Session session, LanDiscovery discovery, Func<LanSession?> lanSession, CourseMaps courseMaps, CarCatalogue carCatalogue)
    {
        _session = session;
        _discovery = discovery;
        _lanSession = lanSession;
```

to:

```csharp
    public MultiplayerPanel(Session session, LanDiscovery discovery, Func<LanSession?> lanSession, Func<RelaySession?> relay, CourseMaps courseMaps, CarCatalogue carCatalogue)
    {
        _session = session;
        _discovery = discovery;
        _lanSession = lanSession;
        _relay = relay;
```

and update the one place `ModeHook` constructs it to pass `() => _relay` as the fourth argument.

`JoinOverTheRelay` sits beside `LeaveRoom`:

```csharp
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
```

Pass the relay in from `ModeHook` where the panel is constructed, alongside the existing `_lanSession` accessor: `() => _relay`.

Finally, in `DrawLobby`, where the host is told how others reach it, add the code:

```csharp
            if (_relay() is { Code.Length: > 0 } mine)
                ImGui.TextDisabled($"Or by code {mine.Code} through the relay");
```

- [ ] **Step 7: Build and run everything**

Run: `dotnet build -c Debug`
Expected: 0 errors.

Run: `dotnet test tests/GT2Port.Tests -c Debug`
Expected: PASS - 12 more than before this task.

- [ ] **Step 8: Commit**

```bash
git add patches/multiplayer tests/GT2Port.Tests
git commit -m "Offer rooms nobody on this network could have announced"
```

---

## Task 7: A room through the relay, end to end

Two sessions, one relay, no direct path between them — the thing the whole plan is for, proved once in a test so it stays true.

**Files:**
- Test: `tests/GT2Port.Tests/RelayedLobbyTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-6.
- Produces: nothing.

- [ ] **Step 1: Write the failing test**

Create `tests/GT2Port.Tests/RelayedLobbyTests.cs`:

```csharp
using System.Net;
using GT2Port.Multiplayer;
using GT2Relay;
using Xunit;

namespace GT2Port.Tests;

/// <summary>
/// A lobby where neither side can address the other.
///
/// Every other test of the lobby has the client send to the host's address.
/// Here there is no such address: both sides speak only to the relay, which is
/// the situation on the internet and the situation this whole stage exists
/// for. Nothing about the lobby itself is new - the point is that none of it
/// had to change.
/// </summary>
public class RelayedLobbyTests : IDisposable
{
    readonly RelayServer _server = new(0);
    DateTime _now = new(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _server.Dispose();

    IPEndPoint Where => new(IPAddress.Loopback, _server.BoundPort);

    void Turn(params Action[] work)
    {
        for (int i = 0; i < 60; i++)
        {
            _server.Pump();
            foreach (var step in work) step();
            _now = _now.AddMilliseconds(50);
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void A_client_joins_a_host_it_has_no_route_to()
    {
        var hostSession = new Session("ian", () => _now);
        hostSession.Host("ian's room", "seattle_short", "special", 6);

        var guestSession = new Session("les", () => _now);

        using var hostRelay = new RelaySession(Where, () => _now);
        using var guestRelay = new RelaySession(Where, () => _now);

        using var hostWire = LanSession.Over(hostRelay, 34719, () => _now,
            hosting: true, ownsTheLink: false);
        using var guestWire = LanSession.Over(guestRelay, 34719, () => _now,
            hosting: false, ownsTheLink: false);

        var room = hostSession.Current!;

        // The host announces, the guest lists, the guest joins - all of it
        // through the relay, which is the only address either side has.
        Turn(
            () => hostRelay.Publish(room.Id, listed: true, RoomState.Serialise(room)),
            () => guestRelay.AskForRooms());

        var advert = Assert.Single(guestRelay.Rooms);
        Assert.Equal(room.Id, advert.Id);
        Assert.True(RoomState.TryDeserialise(advert.Card, out var seen));
        Assert.Equal("ian's room", seen.Name);

        guestRelay.Join(room.Id);
        Turn(() => _ = guestRelay.Available, () => _ = hostRelay.Available);
        Assert.True(guestRelay.Admitted);

        guestSession.Join(seen);

        // From here it is the ordinary lobby, over a link that happens to be
        // relayed. The host's address is whatever the relay told the guest.
        var hostEndPoint = Assert.Single(guestRelay.Peers);

        Turn(
            () => guestWire.ClientTick(guestSession, hostEndPoint.Address),
            () => hostWire.HostTick(hostSession));

        Assert.Contains(hostSession.Current!.Players, p => p.Name == "les");
        Assert.Equal(SessionPhase.Joined, guestSession.Phase);
        Assert.Equal(2, guestSession.Current!.Players.Count);
    }

    /// <summary>
    /// And by code, which is the path for a room that was never listed - the
    /// one somebody pastes into a chat.
    /// </summary>
    [Fact]
    public void A_private_room_is_reached_by_its_code()
    {
        var hostSession = new Session("ian", () => _now);
        hostSession.Host("ian's room", "seattle_short", "special", 6, secret: "hunter2");

        using var hostRelay = new RelaySession(Where, () => _now);
        using var guestRelay = new RelaySession(Where, () => _now);

        var room = hostSession.Current!;

        Turn(() => hostRelay.Publish(room.Id, listed: false, RoomState.Serialise(room)));

        Assert.NotEqual("", hostRelay.Code);

        guestRelay.AskForRooms();
        Turn(() => _ = guestRelay.Available);
        Assert.Empty(guestRelay.Rooms);

        guestRelay.JoinByCode(hostRelay.Code);
        Turn(() => _ = guestRelay.Available, () => _ = hostRelay.Available);

        Assert.True(guestRelay.Admitted);
        Assert.Equal(room.Id, guestRelay.RoomId);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/GT2Port.Tests -c Debug --filter RelayedLobbyTests`
Expected: FAIL if any of Tasks 4-6 is incomplete. If Tasks 1-6 are done, it should pass — in which case check it is really exercising the relay by stopping the server first (`_server.Dispose()` at the top of the test) and confirming it then fails.

- [ ] **Step 3: Make it pass**

No new production code should be needed. If it does not pass, the failure is in Task 5 or Task 6 and belongs there — fix it in the file it belongs to rather than working around it here.

- [ ] **Step 4: Run everything**

Run: `dotnet test tests/GT2Port.Tests -c Debug`
Expected: PASS - 2 more than before this task.

Run: `dotnet test tests/GT2Relay.Tests -c Debug`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/GT2Port.Tests/RelayedLobbyTests.cs
git commit -m "Prove a lobby works with no route between its players"
```

---

## What this deliberately does not do

**NAT-PMP and PCP.** Task 0 speaks UPnP IGD and nothing else. They are a second protocol for the same job, and the routers that speak them and not UPnP are a small enough share to leave alone until somebody turns up who cannot host because of it.

**Hole punching.** Both sides now know each other's public endpoints — the relay told them, and `RelaySession.Peers` holds them. That is everything a direct path needs, and adding one is a change inside `RelaySession` alone: try sending to the peer directly, and if anything comes back that way, prefer it. Nothing above the seam would notice. It is left out here because the relay always works and the punch only sometimes does, and a thing that always works is what this stage is for.

**Authentication.** Anybody who can reach the server can make a room. The caps are what stand between that and a problem. A room that wants to be private already has a secret, which the host checks; the relay's own job is only to carry bytes.

**Encryption.** The traffic is what it always was — a car's position, a lap time, a room's name. If that changes, this is the place it would have to be reconsidered.

**More than one relay.** The address is a single field. Somebody running a second server is a second address to paste, which is fine at this scale and would stop being fine if there were ever a list to choose from.
