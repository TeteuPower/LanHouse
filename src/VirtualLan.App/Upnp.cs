using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace VirtualLan.App;

/// <summary>
/// Cliente UPnP IGD mínimo, "melhor esforço", para abrir a porta do relay no roteador sem o
/// usuário mexer em configuração. Se o roteador não tiver UPnP (ou estiver desligado), falha
/// de forma limpa e o app orienta o encaminhamento manual da porta.
///
/// Sem dependências externas: descoberta SSDP + duas chamadas SOAP (AddPortMapping / DeletePortMapping).
/// </summary>
internal static class Upnp
{
    private static readonly IPEndPoint SsdpEndpoint = new(IPAddress.Parse("239.255.255.250"), 1900);

    public sealed record MapResult(bool Success, string? ExternalIp, string? Detail);

    private sealed record IgdService(Uri ControlUrl, string ServiceType, string GatewayHost);

    public static async Task<MapResult> TryMapUdpAsync(int port, string description, CancellationToken ct)
    {
        try
        {
            var service = await DiscoverAsync(ct).ConfigureAwait(false);
            if (service is null) return new MapResult(false, null, "nenhum roteador UPnP respondeu");

            string localIp = LocalIpTowards(service.GatewayHost);

            bool added = await AddPortMappingAsync(service, port, localIp, description, ct).ConfigureAwait(false);
            string? external = await GetExternalIpAsync(service, ct).ConfigureAwait(false);

            return new MapResult(added, external, added ? null : "o roteador recusou o mapeamento");
        }
        catch (Exception ex)
        {
            return new MapResult(false, null, ex.Message);
        }
    }

    public static async Task TryUnmapUdpAsync(int port, CancellationToken ct)
    {
        try
        {
            var service = await DiscoverAsync(ct).ConfigureAwait(false);
            if (service is null) return;

            await DeletePortMappingAsync(service, port, ct).ConfigureAwait(false);
        }
        catch
        {
            // Limpeza é melhor-esforço: um mapeamento órfão expira sozinho ou pode ser removido no roteador.
        }
    }

    // ------------------------------------------------------------------------ Descoberta SSDP

    private static async Task<IgdService?> DiscoverAsync(CancellationToken ct)
    {
        string[] searchTargets =
        [
            "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1",
        ];

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        foreach (var st in searchTargets)
        {
            byte[] request = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: 239.255.255.250:1900\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 2\r\n" +
                $"ST: {st}\r\n\r\n");

            await udp.SendAsync(request, request.Length, SsdpEndpoint).ConfigureAwait(false);
        }

        while (!timeoutCts.IsCancellationRequested)
        {
            UdpReceiveResult response;
            try
            {
                response = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            string text = Encoding.ASCII.GetString(response.Buffer);
            string? location = ReadHeader(text, "location");
            if (location is null) continue;

            var service = await ResolveServiceAsync(location, ct).ConfigureAwait(false);
            if (service is not null) return service;
        }

        return null;
    }

    private static async Task<IgdService?> ResolveServiceAsync(string descriptionUrl, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string xml = await http.GetStringAsync(descriptionUrl, ct).ConfigureAwait(false);

            var doc = XDocument.Parse(xml);
            var gatewayUri = new Uri(descriptionUrl);

            foreach (var serviceElement in doc.Descendants().Where(e => e.Name.LocalName == "service"))
            {
                string? type = ChildValue(serviceElement, "serviceType");
                string? controlUrl = ChildValue(serviceElement, "controlURL");

                if (type is null || controlUrl is null) continue;
                if (!type.Contains("WANIPConnection") && !type.Contains("WANPPPConnection")) continue;

                var absoluteControl = new Uri(gatewayUri, controlUrl);
                return new IgdService(absoluteControl, type, gatewayUri.Host);
            }
        }
        catch
        {
            // Descrição ilegível: tenta a próxima resposta SSDP.
        }

        return null;
    }

    // ------------------------------------------------------------------------ SOAP

    private static async Task<bool> AddPortMappingAsync(
        IgdService service, int port, string internalClient, string description, CancellationToken ct)
    {
        string body =
            $"<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            $"<NewProtocol>UDP</NewProtocol>" +
            $"<NewInternalPort>{port}</NewInternalPort>" +
            $"<NewInternalClient>{internalClient}</NewInternalClient>" +
            $"<NewEnabled>1</NewEnabled>" +
            $"<NewPortMappingDescription>{Escape(description)}</NewPortMappingDescription>" +
            $"<NewLeaseDuration>0</NewLeaseDuration>";

        var response = await SoapAsync(service, "AddPortMapping", body, ct).ConfigureAwait(false);
        return response is not null;
    }

    private static async Task DeletePortMappingAsync(IgdService service, int port, CancellationToken ct)
    {
        string body =
            $"<NewRemoteHost></NewRemoteHost>" +
            $"<NewExternalPort>{port}</NewExternalPort>" +
            $"<NewProtocol>UDP</NewProtocol>";

        await SoapAsync(service, "DeletePortMapping", body, ct).ConfigureAwait(false);
    }

    private static async Task<string?> GetExternalIpAsync(IgdService service, CancellationToken ct)
    {
        string? response = await SoapAsync(service, "GetExternalIPAddress", "", ct).ConfigureAwait(false);
        if (response is null) return null;

        try
        {
            var value = XDocument.Parse(response).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value;

            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> SoapAsync(IgdService service, string action, string innerBody, CancellationToken ct)
    {
        string envelope =
            "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
            "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body>" +
            $"<u:{action} xmlns:u=\"{service.ServiceType}\">{innerBody}</u:{action}>" +
            "</s:Body></s:Envelope>";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var content = new StringContent(envelope, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPAction", $"\"{service.ServiceType}#{action}\"");

            using var response = await http.PostAsync(service.ControlUrl, content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------------ Utilitários

    private static string LocalIpTowards(string gatewayHost)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(gatewayHost, 1900);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch
        {
            return IPAddress.Loopback.ToString();
        }
    }

    private static string? ChildValue(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static string? ReadHeader(string httpResponse, string headerName)
    {
        foreach (var line in httpResponse.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;

            if (line[..colon].Trim().Equals(headerName, StringComparison.OrdinalIgnoreCase))
                return line[(colon + 1)..].Trim();
        }

        return null;
    }

    private static string Escape(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
