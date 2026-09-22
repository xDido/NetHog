namespace WifiBox.Models;

public sealed record NetworkSnapshot(
    string InterfaceName,
    string InterfaceDescription,
    string InterfaceId,
    string LocalAddress,
    string LocalMacAddress,
    string GatewayAddress,
    string GatewayMacAddress,
    int PrefixLength,
    bool HasIpv6,
    int HostsProbed,
    DateTimeOffset ScannedAt,
    IReadOnlyList<NetworkDevice> Devices);
