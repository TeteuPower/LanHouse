using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VirtualLan.Node.Peers;

/// <summary>
/// Descobre os endereços IPv4 das interfaces físicas da máquina.
///
/// Servem para o caso de os dois peers estarem na mesma LAN física: o hole punching contra
/// o endereço público falharia (muitos roteadores domésticos não fazem hairpin NAT), mas o
/// caminho pelo endereço local funciona de imediato.
/// </summary>
public static class LocalEndpointDiscovery
{
    public static List<IPEndPoint> Discover(int localPort, string excludeAdapterName)
    {
        List<IPEndPoint> endpoints = [];

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

            // Nunca anunciar o endereço da própria TAP: isso criaria um laço de encapsulamento.
            if (string.Equals(nic.Name, excludeAdapterName, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(unicast.Address)) continue;
                if (unicast.Address.GetAddressBytes() is [169, 254, ..]) continue; // APIPA

                endpoints.Add(new IPEndPoint(unicast.Address, localPort));
            }
        }

        return endpoints;
    }
}
