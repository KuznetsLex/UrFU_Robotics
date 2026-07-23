using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Team11.Ros
{
    /// <summary>
    /// Resolves robot 158 without relying on the phone hotspot keeping the same
    /// DHCP subnet. mDNS is attempted first; a small identity service on the
    /// Raspberry Pi provides a unicast fallback when the hotspot blocks mDNS.
    /// </summary>
    public static class RobotEndpointDiscovery
    {
        public const string HostName = "robocamp-158.local";
        private const int DiscoveryPort = 10003;
        private const string ExpectedResponse = "ROBOCAMP_158";
        private const int ProbeTimeoutMilliseconds = 600;

        private static readonly object Sync = new object();
        private static Task<string> resolveTask;

        public static string CurrentHost { get; private set; } = HostName;

        public static Task<string> ResolveAsync()
        {
            lock (Sync)
            {
                if (resolveTask == null || resolveTask.IsFaulted || resolveTask.IsCanceled)
                {
                    resolveTask = ResolveCoreAsync();
                }

                return resolveTask;
            }
        }

        private static async Task<string> ResolveCoreAsync()
        {
            string mdnsAddress = await ResolveMdnsAsync();
            if (!string.IsNullOrEmpty(mdnsAddress) &&
                await ProbeAsync(mdnsAddress) != null)
            {
                CurrentHost = mdnsAddress;
                return CurrentHost;
            }

            var candidates = GetLocalSubnetCandidates()
                .Select(ProbeAsync)
                .ToList();

            while (candidates.Count > 0)
            {
                Task<string> completed = await Task.WhenAny(candidates);
                candidates.Remove(completed);
                string address = await completed;
                if (!string.IsNullOrEmpty(address))
                {
                    CurrentHost = address;
                    return CurrentHost;
                }
            }

            // This keeps the normal DNS path available on networks where mDNS
            // starts working after Unity has entered Play Mode.
            CurrentHost = HostName;
            return CurrentHost;
        }

        private static async Task<string> ResolveMdnsAsync()
        {
            try
            {
                Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(HostName);
                if (await Task.WhenAny(lookup, Task.Delay(ProbeTimeoutMilliseconds)) != lookup)
                {
                    return null;
                }

                return (await lookup)
                    .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
                    ?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> GetLocalSubnetCandidates()
        {
            var addresses = new HashSet<string>();

            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties properties = adapter.GetIPProperties();
                bool hasIpv4Gateway = properties.GatewayAddresses.Any(
                    gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                               !gateway.Address.Equals(IPAddress.Any));
                if (!hasIpv4Gateway)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    byte[] octets = unicast.Address.GetAddressBytes();
                    for (int host = 1; host < 255; host++)
                    {
                        if (host != octets[3])
                        {
                            addresses.Add($"{octets[0]}.{octets[1]}.{octets[2]}.{host}");
                        }
                    }
                }
            }

            return addresses;
        }

        private static async Task<string> ProbeAsync(string host)
        {
            using (var client = new TcpClient())
            {
                try
                {
                    Task connect = client.ConnectAsync(host, DiscoveryPort);
                    if (await Task.WhenAny(connect, Task.Delay(ProbeTimeoutMilliseconds)) != connect)
                    {
                        return null;
                    }
                    await connect;

                    byte[] response = new byte[64];
                    Task<int> read = client.GetStream().ReadAsync(response, 0, response.Length);
                    if (await Task.WhenAny(read, Task.Delay(ProbeTimeoutMilliseconds)) != read)
                    {
                        return null;
                    }

                    string marker = Encoding.ASCII.GetString(response, 0, await read).Trim();
                    if (!string.Equals(marker, ExpectedResponse, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    return ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                }
                catch
                {
                    return null;
                }
            }
        }
    }
}
