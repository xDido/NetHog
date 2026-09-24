# NetHog

NetHog is a Windows 10/11 network monitor and bandwidth-control dashboard—and a modern **SelfishNet alternative** for home networks and small-network administrators. Discover devices on your LAN, watch live traffic, temporarily limit upload/download speeds, or block routed IPv4 internet access from one Windows PC.

NetHog is not affiliated with SelfishNet. It uses a modern WPF interface, Npcap, and temporary network controls instead of requiring a compatible router API.

## Features

- Discover devices on the selected Ethernet or Wi-Fi network and show IP addresses, MAC addresses, nicknames, proximity, and live traffic rates.
- Apply temporary per-device/multiple devices upload/download limits or a routed IPv4 block through the Windows PC.
- See the active control asserted on a PC from another NetHog instance on the same LAN. A person using that controlled PC can remove its own controls locally; a controller does not receive a request to approve the removal.
- Observe destination metadata from DNS questions, TLS SNI, and HTTP Host values without decrypting HTTPS traffic.
- Block or unblock observed domains and subdomains, with IPv4 matching.
- View a rolling 15-minute live traffic chart.
- Review session history, compare sessions, export CSV/JSON, delete history, and configure retention.
- Save reusable control presets.
- Show an optional always-on-top traffic overlay with position, opacity, display, and click-through settings.
- Configure traffic alerts, bits/bytes units, dark mode, startup behavior, tray minimization, automatic updates, and history retention.
  
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

The release archive also includes a portable executable in the `latest` folder.

## What the controls do

Peer-device controls use ARP-based interception through the Windows PC and apply temporary routed IPv4 upload/download limits or an IPv4 block. They are intended for short-lived sessions, not permanent router configuration.

The current PC has a separate Windows policy path. Windows can limit the current PC's outbound/upload traffic, but the built-in policy cannot reliably shape the current PC's inbound/download traffic. Same-LAN traffic, IPv6 traffic, VPN traffic, network isolation, and destinations that do not pass through the selected path may not be affected.

ARP restoration is best effort. If NetHog or Windows is forcibly terminated, a device may temporarily retain stale ARP information until the network refreshes it; restarting the control session or using **Restore network** normally resolves this.

When a device is running NetHog and another machine has applied a control to it, the controlled instance reports that state. The person on the controlled machine can remove the control from that machine without approval from the controlling instance.

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

## Compatibility

NetHog is intended for Windows 10 and Windows 11 x64 systems using an Ethernet or Wi-Fi adapter. Network behavior can vary with router configuration, wireless client isolation, VPNs, security software, and other traffic-management tools.

## License

NetHog is released under the [MIT License](LICENSE).
