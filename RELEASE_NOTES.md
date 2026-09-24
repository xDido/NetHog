# NetHog v0.2.0

NetHog is now a modern Windows **SelfishNet alternative** for people who administer a home or small local network. This release replaces the original WifiBox application with the new NetHog dashboard and a broader set of network-monitoring tools.

## Highlights

- Replaced the old interface with the NetHog WPF dashboard for Ethernet/Wi-Fi adapter selection, device discovery, IP/MAC details, nicknames, proximity, live rates, and per-device traffic totals.
- Added temporary peer-device upload/download limits and routed IPv4 blocking through the selected Windows PC, including bulk controls for eligible devices.
- Added current-PC controls through Windows local policy, with clear communication that inbound/download shaping is not available through that Windows path.
- Added LAN control coordination: a NetHog instance on a controlled PC can see the active control asserted by another machine and remove its own control locally without requesting approval from the controller.
- Added the Domains view for visible DNS questions, TLS SNI, and HTTP Host metadata, with IPv4 domain/subdomain blocking and no HTTPS decryption.
- Added a rolling 15-minute live traffic chart and a traffic overlay with configurable position, opacity, display, and click-through settings.
- Added session history, session comparison, CSV/JSON export, deletion, retention settings, and cumulative per-device traffic totals.
- Added reusable control presets and expanded settings for units, alerts, dark mode, startup, tray behavior, automatic updates, and history retention.
- Improved the UI layout and accessibility: left navigation, clearer IPv4 control warnings, stable device-table selection, corrected control-button contrast, and fixed settings-field truncation.
- Fixed local restore handling so missing NetHog policies are treated as already restored instead of as a failed removal.
- Added a normal per-machine MSI installer with Start menu integration, uninstall cleanup, and entries in Windows Settings and Control Panel. Releases include the MSI and a portable executable in one ZIP.
- Added release signing hooks for the MSI/EXE and release archive, with documentation for public certificate and updater-signing secrets.

## Important notes

- Windows 10/11 x64 and Npcap are required.
- Controls are temporary and depend on adapter/router behavior; IPv4 is the enforced protocol path.
- Same-LAN traffic, IPv6, VPN traffic, client isolation, and destinations that do not pass through the selected gateway may bypass controls.
- ARP restoration is best effort after a forced termination.
- NetHog is not affiliated with SelfishNet.
