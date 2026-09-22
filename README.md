# NetHog

NetHog is a Windows 10/11 desktop app for finding devices on the active Ethernet or Wi-Fi network and applying temporary IPv4 upload/download limits or a routed-traffic block.

## What is implemented

- Selects the adapter Windows uses for its default IPv4 route, including wired Ethernet, and scans up to 1,022 subnet addresses. Sleeping devices, guest networks, client isolation, and larger subnets can hide devices.
- Applies per-device upload and download shaping (whole Mbps, from 1 to 10,000) or drops IPv4 traffic routed through the network gateway.
- Uses an explicit network-administrator confirmation and a separate start-session action. No client is affected until a device rule is applied.
- Stops the session when NetHog closes gracefully, the adapter/IP changes, or packet forwarding fails, then sends ARP restoration updates to affected peers.
- Does not persist rules. Restarting NetHog starts with a clean rule list.

## Portable app

The x64 release is a single-file, self-contained executable at `artifacts/latest/NetHog.exe`. Copy it to a folder or USB drive and run it on Windows 10/11 x64; you do not need to install the .NET runtime or run a NetHog setup wizard. NetHog requests Windows administrator approval when launched.

The app itself is portable, but packet controls still need Npcap installed once on the Windows PC. Npcap is a system network driver, not a library that can be carried beside the EXE. Download it from the [official Npcap page](https://npcap.com/#download). NetHog continues to support device scanning if Npcap is absent, but leaves control-session actions disabled.

Npcap's free build is not licensed for redistribution, and its silent installer is an OEM feature. This package therefore does not bundle or silently install Npcap. A truly single-file distribution with automatic driver setup would require an appropriate Npcap OEM redistribution license, or a different control architecture.

## Requirements and use

1. For speed limits and blocking, install Npcap on the Windows PC. NetHog uses Npcap through SharpPcap for packet capture and injection.
2. Connect to the Ethernet or Wi-Fi network you administer and launch NetHog. Windows will ask for administrator approval because packet capture and injection require elevation.
3. Scan the network, check the network-administrator confirmation, and start a control session.
4. Choose **Set controls** for a visible device, enter either speed limit or enable **Block internet access**, then apply.
5. Choose **Stop control session** when finished. Closing NetHog also attempts to stop the session and restore ARP entries.

Blank speed fields mean unlimited in that direction. Limits are whole Mbps. The block drops IPv4 traffic this device routes through the Wi-Fi gateway; the ordinary same-subnet local traffic is not redirected by this method, but traffic to other routed networks can be affected too.

## Compatibility and cleanup limits

This first version uses temporary ARP-based interception instead of a router-specific API. Some access points, switches, client-isolation settings, proxy ARP, VPNs, adapters, and Npcap/driver combinations prevent reliable enforcement. IPv4 and IPv6 traffic can be observed when Npcap exposes the frames, but controls remain IPv4-only and IPv6 can bypass them. The interface reports these limitations.

ARP restoration is best effort: NetHog sends repeated corrections when a rule is removed or the session stops. If Windows is force-killed, the PC loses power, or the adapter disappears before restoration, a peer's ARP cache may remain stale until it refreshes. Use the explicit stop action before disconnecting or changing networks. The application has been build-verified, but its packet forwarding still needs live validation on each intended Windows/network-adapter/Npcap setup before relying on it.

## Build

Install the .NET 8 SDK and open `NetHog.sln` in Visual Studio 2022, or run `dotnet build NetHog.sln` in a Windows terminal.

To publish a self-contained single-file x64 Windows build into the artifact folder:

```powershell
dotnet publish .\src\WifiBox\NetHog.csproj -c Release -r win-x64 --self-contained true `
  --source https://api.nuget.org/v3/index.json -o .\artifacts\latest
```

Launch the rebuilt app from `artifacts\latest\NetHog.exe`. Close any older NetHog window first so Windows does not keep the old artifact open.

## Release verification

Release checksums are stored in [`checksums/NetHog-v0.1.1.txt`](checksums/NetHog-v0.1.1.txt). Verify a downloaded executable in PowerShell with:

```powershell
Get-FileHash .\NetHog.exe -Algorithm MD5
Get-FileHash .\NetHog.exe -Algorithm SHA256
```

The device table in the current build has one IP column, one MAC column with a small inline nickname editor, a current download/upload rate field, and controls. Current rates are observed after the Npcap control session is started; before that, the app reports that monitoring is inactive.

Each device also shows a best-effort **network proximity** sample based on ICMP round-trip latency. This is a reachability indicator, not a physical meter reading. RuView-style physical distance and room sensing requires Wi-Fi CSI from dedicated ESP32/research hardware; ordinary Windows Wi-Fi adapters expose no per-client CSI.

NetHog makes generic name suggestions from local reverse DNS and NetBIOS hostnames when available. Phones that use randomized MAC addresses or do not publish a hostname remain identifiable by IP and MAC until you name them manually; the DHCP lease database itself remains router-specific.

The app uses C# and WPF for its Windows GUI. The packet capture/injection engine is provided by Npcap; NetHog does not install a custom kernel driver.

NetHog settings can choose bits-per-second or bytes-per-second live rates, decimal or binary total-size units, register the published executable in the current Windows user's startup list, and choose whether closing the window hides it in the notification area. Choosing **Exit** from the tray always performs a full shutdown.
