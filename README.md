# NetHog

NetHog is a Windows 10/11 desktop app for finding devices on the active Ethernet or Wi-Fi network, viewing live and cumulative session traffic, and applying temporary IPv4 controls.

## Features

- Scans the active IPv4 subnet and identifies visible devices by IP, MAC, and name.
- Shows current rates and per-device totals for the active control session.
- Keeps completed-session history in the app and exposes it from **Session history**.
- Applies temporary upload/download limits or a routed IPv4 block to other devices through Npcap.
- Applies a current-PC upload limit or outbound block through built-in Windows policy. Windows cannot shape the current PC's inbound traffic through this path.
- Offers traffic-unit, startup, tray, and automatic-update settings.

Controls are temporary. Stopping or closing NetHog attempts ARP restoration and removes the current-PC Windows policy. A forced termination can leave a peer's ARP cache stale until it refreshes.

## Downloads

Each GitHub release provides one Windows x64 archive:

`release-v<version>.zip`, containing `latest\NetHog.exe` (portable) and `installer\NetHog-<version>-x64.msi` (installer).

The MSI removes the installed files and NetHog's settings, nicknames, and session history from the current user's app-data folder during uninstall. NetHog does not create cache or temp files; its JSON files are intentional user data.

Packet controls require Npcap, which is installed separately from the [official Npcap page](https://npcap.com/#download). The free Npcap build is not redistributed by this project.

## Build

Install the .NET 8 SDK and run:

```powershell
dotnet build .\NetHog.sln
.\publish.ps1
```

The build uses temporary staging files and leaves only `artifacts\release-v<version>.zip`. To build the MSI and release archive, install WiX Toolset 4 and run `installer\build-msi.ps1`.

## Contributions

Contributions are welcome. Please open an issue before large changes, keep network-control behavior explicit about its limits, and include focused tests or verification notes with pull requests.

## Compatibility

NetHog targets Windows 10/11 x64 and controls IPv4 traffic. IPv6, client isolation, router behavior, VPNs, and adapter/Npcap compatibility can affect enforcement. Validate the build on the intended network before relying on it.
