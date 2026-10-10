using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Communication.App.Presentation;

public sealed record IpAddressPreset(string Description, string Address);

public static class IpAddressPresets
{
    public static Task<IReadOnlyList<IpAddressPreset>> DiscoverLocalAsync(CancellationToken cancellationToken = default) => Task.Run<IReadOnlyList<IpAddressPreset>>(() =>
    {
        var addresses = new List<IpAddressPreset>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().OrderBy(adapter => adapter.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            IPInterfaceProperties properties;
            try
            {
                properties = adapter.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var address in properties.UnicastAddresses.Select(item => item.Address)
                .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Where(address => !IPAddress.IsLoopback(address) && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
                .OrderBy(address => address.AddressFamily).ThenBy(address => address.ToString(), StringComparer.Ordinal))
            {
                var family = address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6";
                // ToString retains the scope ID required to bind IPv6 link-local addresses.
                var preset = new IpAddressPreset($"{adapter.Name} · {family}", address.ToString());
                if (!addresses.Contains(preset))
                    addresses.Add(preset);
            }
        }
        return addresses;
    }, cancellationToken);
    public static IReadOnlyList<IpAddressPreset> Bind
    {
        get;
    } =
    [
        new("全部本地网卡（IPv4）", "0.0.0.0"), new("本机回环（IPv4）", "127.0.0.1"),
        new("全部本地网卡（IPv6）", "::"), new("本机回环（IPv6）", "::1")
    ];
    public static IReadOnlyList<IpAddressPreset> Remote
    {
        get;
    } =
    [new("本机回环（IPv4）", "127.0.0.1"), new("本机回环（IPv6）", "::1"), new("本机主机名", "localhost")];
    public static IReadOnlyList<IpAddressPreset> UdpV4
    {
        get;
    } =
    [new("本机回环（IPv4）", "127.0.0.1"), new("局域网广播（IPv4）", "255.255.255.255")];
    public static IReadOnlyList<IpAddressPreset> UdpV6
    {
        get;
    } =
    [new("本机回环（IPv6）", "::1")];
}
