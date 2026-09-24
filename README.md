# NetHog

NetHog is a Windows 10/11 network monitor and bandwidth-control dashboard—and a modern **SelfishNet alternative** for home networks and small-network administrators. Discover devices on your LAN, watch live traffic, temporarily limit upload/download speeds, or block routed IPv4 internet access from one Windows PC.

NetHog is not affiliated with SelfishNet. It uses a modern WPF interface, Npcap, and temporary network controls instead of requiring a compatible router API.

## Why use NetHog?

If you are looking for a SelfishNet alternative, NetHog provides the same general kind of LAN device visibility and temporary per-device control in an actively maintained Windows application. It is also useful as a Windows bandwidth limiter, Wi-Fi/LAN device monitor, network traffic monitor, or temporary internet blocker.

NetHog is designed for networks that you administer. It does not replace router QoS, a firewall, or enterprise network management, and its controls have the limitations described below.

## Features

- Discover devices on the selected Ethernet or Wi-Fi network and show IP addresses, MAC addresses, nicknames, proximity, and live traffic rates.
- Apply temporary per-device upload/download limits or a routed IPv4 block through the Windows PC.
- Apply one control rule to multiple selected devices.
- See the active control asserted on a PC from another NetHog instance on the same LAN. A person using that controlled PC can remove its own controls locally; a controller does not receive a request to approve the removal.
- Observe destination metadata from DNS questions, TLS SNI, and HTTP Host values without decrypting HTTPS traffic.
- Block or unblock observed domains and subdomains, with IPv4 matching.
- View a rolling 15-minute live traffic chart.
- Review session history, compare sessions, export CSV/JSON, delete history, and configure retention.
- Save reusable control presets.
- Show an optional always-on-top traffic overlay with position, opacity, display, and click-through settings.
- Configure traffic alerts, bits/bytes units, dark mode, startup behavior, tray minimization, automatic updates, and history retention.
- Install and uninstall through a normal Windows MSI. The app is listed in Windows Settings and Control Panel after installation.

## Installation

### Requirements

- Windows 10 or Windows 11, 64-bit.
- [Npcap](https://npcap.com/#download). NetHog uses Npcap for network capture and observation; Npcap is not bundled with this repository.
- Administrator permission when starting a control session or applying Windows network controls.

### Install the latest release

1. Open the [latest NetHog release](https://github.com/xDido/NetHog/releases/latest) and download the `release-v<version>.zip` asset.
2. Extract the archive to a folder you control.
3. Open the `installer` folder and run `NetHog-<version>-x64.msi`.
4. Accept the Windows elevation prompt and complete the setup.
5. Start NetHog from the Start menu. To remove it later, use **Settings > Apps > Installed apps** or **Control Panel > Programs and Features**.

The release archive also includes a portable executable in the `latest` folder. The MSI is recommended because it creates the normal Windows installation and uninstall entry.

### First run

1. Install Npcap first, then restart Windows if Npcap requests it.
2. Launch NetHog and choose the active Ethernet or Wi-Fi adapter/default route.
3. Select **Scan network** to discover nearby devices.
4. Confirm **I administer this network and authorize temporary IPv4 controls for selected devices** only when you administer the network.
5. Start a control session, select a device, and choose **Set controls**. You can also select several eligible devices for a bulk rule.
6. Use **Restore network** when you want to remove the controls from the network.

When a device is running NetHog and another machine has applied a control to it, the controlled instance reports that state. The person on the controlled machine can remove the control from that machine without approval from the controlling instance.

## What the controls do

Peer-device controls use ARP-based interception through the Windows PC and apply temporary routed IPv4 upload/download limits or an IPv4 block. They are intended for short-lived sessions, not permanent router configuration.

The current PC has a separate Windows policy path. Windows can limit the current PC's outbound/upload traffic, but the built-in policy cannot reliably shape the current PC's inbound/download traffic. Same-LAN traffic, IPv6 traffic, VPN traffic, network isolation, and destinations that do not pass through the selected path may not be affected.

ARP restoration is best effort. If NetHog or Windows is forcibly terminated, a device may temporarily retain stale ARP information until the network refreshes it; restarting the control session or using **Restore network** normally resolves this.

## Domains, traffic, and history

The Domains view records destination metadata that is visible on the network, including DNS questions, TLS SNI, and HTTP Host values. NetHog does not decrypt HTTPS traffic. Domain blocking is based on observed IPv4 destinations; IPv6 destinations are observed but are not blocked by this feature.

The Live chart keeps a rolling 15-minute traffic timeline in memory. Completed sessions can be reviewed and compared in Session history, exported as CSV or JSON, deleted, or removed automatically according to the retention setting.

## Troubleshooting

- **No devices appear:** confirm that the selected adapter is connected to the same LAN, disable conflicting VPN/virtual adapters temporarily, and check whether client isolation is enabled on the access point. Sleeping or silent devices may not respond to discovery.
- **Controls are unavailable:** run NetHog with administrator permission, confirm Npcap is installed, confirm that you administer the selected network, and use an IPv4 network path.
- **The current PC cannot be download-limited:** this is a Windows policy limitation; NetHog can limit the current PC's outbound/upload traffic through its local policy path.
- **Restore reports a Windows policy error:** NetHog now checks whether its policies exist before attempting removal. If another security product blocks local traffic-policy commands, Windows may still reject the restore operation.
- **Windows shows an unknown publisher warning:** a public code-signing certificate is required for a trusted publisher name. Local development builds use a local certificate only after it is explicitly trusted on that machine; verify downloaded release assets and their source.

## Build from source

The project targets .NET 8 and builds the Windows application plus an MSI with WiX Toolset 4. On Windows PowerShell, install the .NET 8 SDK and WiX Toolset 4, then run:

```powershell
.\installer\build-msi.ps1
```

If `signtool.exe` is not already available from the Windows SDK, the optional helper downloads the Microsoft SDK build tools into the local `artifacts` folder:

```powershell
.\installer\get-signing-tools.ps1
```

The local build creates `artifacts\release-v<version>.zip` containing the MSI and portable executable.

### Avalonia cross-platform beta

The `linux-beta` branch also contains an Avalonia desktop target at `src/NetHog.Avalonia`. It uses the same device models and ARP packet-control engine as the Windows app, with portable adapter discovery for Windows and Linux.

On Linux, install the .NET 8 SDK and libpcap first. Debian/Ubuntu users can run:

```bash
sudo apt install libicu-dev libpcap0.8 libpcap-dev
```

Run the beta from the repository with:

```bash
dotnet run --project src/NetHog.Avalonia/NetHog.Avalonia.csproj
```

Build a self-contained package for either desktop platform with:

```bash
dotnet publish src/NetHog.Avalonia/NetHog.Avalonia.csproj -c Release -r linux-x64 --self-contained true
dotnet publish src/NetHog.Avalonia/NetHog.Avalonia.csproj -c Release -r win-x64 --self-contained true
```

The Linux beta needs root or the equivalent packet-capture capabilities to start a control session. Its cross-platform shell is being expanded page by page; the existing WPF application remains the production Windows target until the Avalonia surface reaches feature parity.

## Privacy and local data

NetHog's settings, device profiles, presets, and session history are stored locally on the computer. Network observations and traffic history are used by the local application; NetHog does not require a cloud account for its core monitoring and control features.

## Compatibility

NetHog is intended for Windows 10 and Windows 11 x64 systems using an Ethernet or Wi-Fi adapter and an IPv4 network path. Network behavior can vary with router configuration, wireless client isolation, VPNs, security software, and other traffic-management tools.

## License

NetHog is released under the [MIT License](LICENSE).
