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
