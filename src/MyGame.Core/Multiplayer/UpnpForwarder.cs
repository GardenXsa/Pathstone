using System;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace MyGame.Core.Multiplayer;

/// <summary>
/// Attempts UPnP/IGD port forwarding via SSDP discovery + SOAP requests.
/// Issue #29. Best-effort — silently falls back to manual forwarding if
/// UPnP is unavailable (no router, router doesn't support UPnP, etc.).
///
/// <para>
/// No external NuGet packages — uses raw HTTP + SSDP multicast. This is
/// a minimal implementation that works with most consumer routers.
/// </para>
/// </summary>
public static class UpnpForwarder
{
    private const string SsdpMulticastAddress = "239.255.255.250";
    private const int SsdpPort = 1900;
    private static readonly TimeSpan DiscoverTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Try to discover the IGD (Internet Gateway Device) on the local
    /// network via SSDP, then request a port mapping. Returns the public
    /// IP:port if successful, null if UPnP is unavailable.
    /// </summary>
    public static async Task<UpnpResult?> TryForwardAsync(
        int internalPort, int externalPort, string description, CancellationToken ct = default)
    {
        try
        {
            // 1. SSDP discover — send M-SEARCH multicast, wait for response.
            var controlUrl = await DiscoverIgdAsync(ct).ConfigureAwait(false);
            if (controlUrl is null) return null;

            // 2. Get external IP.
            var externalIp = await GetExternalIpAsync(controlUrl, ct).ConfigureAwait(false);
            if (externalIp is null) return null;

            // 3. Add port mapping.
            var ok = await AddPortMappingAsync(controlUrl, internalPort, externalPort, description, ct)
                .ConfigureAwait(false);
            if (!ok) return null;

            return new UpnpResult
            {
                PublicIp = externalIp,
                ExternalPort = externalPort,
                ControlUrl = controlUrl,
            };
        }
        catch
        {
            return null; // best-effort
        }
    }

    private static async Task<string?> DiscoverIgdAsync(CancellationToken ct)
    {
        using var udp = new System.Net.Sockets.UdpClient();
        udp.Client.ReceiveTimeout = (int)DiscoverTimeout.TotalMilliseconds;

        var searchMessage = Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n" +
            $"HOST: {SsdpMulticastAddress}:{SsdpPort}\r\n" +
            "MAN: \"ssdp:discover\"\r\n" +
            "MX: 2\r\n" +
            "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");

        var endpoint = new IPEndPoint(IPAddress.Parse(SsdpMulticastAddress), SsdpPort);
        await udp.SendAsync(searchMessage, searchMessage.Length, endpoint).ConfigureAwait(false);

        // Wait for a response (best-effort, short timeout).
        var receiveTask = udp.ReceiveAsync();
        var completed = await Task.WhenAny(receiveTask, Task.Delay(DiscoverTimeout, ct)).ConfigureAwait(false);

        // Timeout path: the receive task is still pending. We must observe
        // it before the `using var udp` block disposes the UdpClient —
        // otherwise disposal aborts the pending ReceiveAsync, throwing a
        // SocketException ("Операция ввода-вывода была прервана...") that
        // nobody awaits, surfacing later as a TaskScheduler.UnobservedTaskException
        // crash dump. We do this by awaiting receiveTask with a try/catch
        // (it will fault when udp is disposed below, which we swallow).
        if (completed != receiveTask)
        {
            try { await receiveTask.ConfigureAwait(false); }
            catch { /* expected — udp will be disposed below */ }
            return null;
        }

        // Success path — receiveTask already completed; access .Result safely.
        UdpReceiveResult result;
        try
        {
            result = receiveTask.Result;
        }
        catch (AggregateException)
        {
            return null;
        }
        var response = result.Buffer;
        var responseStr = Encoding.ASCII.GetString(response);

        // Extract the LOCATION header.
        var match = Regex.Match(responseStr, @"LOCATION:\s*(\S+)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var deviceUrl = match.Groups[1].Value;

        // Fetch the device description XML to find the WANIPConnection control URL.
        using var http = new HttpClient { Timeout = DiscoverTimeout };
        var descXml = await http.GetStringAsync(deviceUrl, ct).ConfigureAwait(false);
        var doc = XDocument.Parse(descXml);

        // Find the WANIPConnection service.
        var ns = XNamespace.Get("urn:schemas-upnp-org:device-1-0");
        var service = doc.Descendants(ns + "service")
            .FirstOrDefault(s => s.Element(ns + "serviceType")?.Value
                ?.Contains("WANIPConnection") == true
                || s.Element(ns + "serviceType")?.Value
                ?.Contains("WANPPPConnection") == true);

        if (service is null) return null;

        var controlPath = service.Element(ns + "controlURL")?.Value;
        if (string.IsNullOrWhiteSpace(controlPath)) return null;

        // Combine with the device URL base.
        var baseUri = new Uri(deviceUrl);
        var controlUri = new Uri(baseUri, controlPath);
        return controlUri.ToString();
    }

    private static async Task<string?> GetExternalIpAsync(string controlUrl, CancellationToken ct)
    {
        var soap = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
<s:Body>
<u:GetExternalIPAddress xmlns:u=""urn:schemas-upnp-org:service:WANIPConnection:1""></u:GetExternalIPAddress>
</s:Body>
</s:Envelope>";

        using var http = new HttpClient { Timeout = DiscoverTimeout };
        var content = new StringContent(soap, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", "\"urn:schemas-upnp-org:service:WANIPConnection:1#GetExternalIPAddress\"");

        var response = await http.PostAsync(controlUrl, content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var match = Regex.Match(body, @"<NewExternalIPAddress>([^<]+)</NewExternalIPAddress>");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static async Task<bool> AddPortMappingAsync(
        string controlUrl, int internalPort, int externalPort, string description, CancellationToken ct)
    {
        var localIp = GetLocalIp();
        var soap = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
<s:Body>
<u:AddPortMapping xmlns:u=""urn:schemas-upnp-org:service:WANIPConnection:1"">
<NewRemoteHost></NewRemoteHost>
<NewExternalPort>{externalPort}</NewExternalPort>
<NewProtocol>TCP</NewProtocol>
<NewInternalPort>{internalPort}</NewInternalPort>
<NewInternalClient>{localIp}</NewInternalClient>
<NewEnabled>1</NewEnabled>
<NewPortMappingDescription>{description}</NewPortMappingDescription>
<NewLeaseDuration>86400</NewLeaseDuration>
</u:AddPortMapping>
</s:Body>
</s:Envelope>";

        using var http = new HttpClient { Timeout = DiscoverTimeout };
        var content = new StringContent(soap, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", "\"urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping\"");

        var response = await http.PostAsync(controlUrl, content, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private static string GetLocalIp()
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            socket.Connect("8.8.8.8", 80);
            var endPoint = socket.LocalEndPoint as IPEndPoint;
            return endPoint?.Address.ToString() ?? "192.168.1.100";
        }
        catch { return "192.168.1.100"; }
    }
}

/// <summary>Result of a successful UPnP port forward.</summary>
public sealed record UpnpResult
{
    public required string PublicIp { get; init; }
    public required int ExternalPort { get; init; }
    public required string ControlUrl { get; init; }

    /// <summary>Public IP:port string for display.</summary>
    public string PublicAddress => $"{PublicIp}:{ExternalPort}";
}
