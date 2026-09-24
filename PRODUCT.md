# Product

<!-- impeccable:product-schema 1 -->

## Platform

Cross-platform desktop (Windows 10/11 and Linux)

## Stack

C# / .NET 8 / Avalonia UI, with platform adapters for Windows and Linux.

## Users

People who own or administer a local network and want to manage its connected devices. (Home use is inferred from the request.)

## Product Purpose

Discover devices on the active Ethernet or Wi-Fi network, view their activity, apply per-device IPv4 upload and download limits, or block their internet access.

## Positioning

The first version uses ARP-based interception to work across many router brands without depending on each router's management API. Router, adapter, and network compatibility cannot be guaranteed.

## Operating Context

The operator runs the app on Windows or Linux while connected to the Ethernet or Wi-Fi network they administer. The app follows the adapter used for the host's default IPv4 route. Network controls require elevated permission and explicit confirmation that the operator administers the network.

## Capabilities and Constraints

- Discover connected devices and show available IP, MAC, name, and traffic information.
- Apply session-only upload and download limits in Mbps, or block internet access for a device. Windows uses its local policy path for current-PC outbound throttling/blocking; Linux uses the host traffic-control and firewall tools when available.
- Advertise active peer controls to a controlled device's NetHog instance and let that device remove its own control directly without controller approval.
- Show cumulative per-device traffic for the active session and preserve completed session history in the app.
- Ship a portable executable and an MSI installer through GitHub releases.
- Do not create cache or temp files; settings, profiles, and history are intentional app data.
- The first version controls IPv4. Detect IPv6 and warn that IPv4 rules may be bypassed over IPv6.
- Controls depend on adapter and router behavior; the app must communicate when enforcement is unavailable.
- Same-subnet local traffic is outside the interception path. Other IPv4 destinations routed by the gateway can also be affected by a block.
- Npcap provides packet capture and injection on Windows; libpcap provides the equivalent capture path on Linux. NetHog does not ship its own kernel driver.
- The .NET application is distributed as a self-contained executable. Packet capture remains a separate system prerequisite: Npcap on Windows and libpcap on Linux.
- ARP restoration on graceful stop is best effort. A forced process termination can leave stale peer ARP entries temporarily.

## Evidence on Hand

The repository started empty. No product assets, customer claims, or verified router compatibility data were provided; do not invent them.

## Product Principles

- Make the current network and active control state easy to verify.
- Never imply a device is limited or blocked when enforcement is unavailable.
- Make temporary controls and their network effects clear to the operator.
