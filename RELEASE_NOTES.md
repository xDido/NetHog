# NetHog v0.1.1

NetHog is a portable Windows 10/11 network dashboard for discovering visible devices, naming clients, viewing live traffic rates, applying temporary IPv4 controls, and managing startup/tray behavior.

Highlights:

- Ethernet and Wi-Fi device discovery with IPv4 and IPv6 addresses.
- Inline MAC-address nicknames and generic hostname suggestions.
- Current per-device and aggregate traffic rates with configurable rate and size units.
- Best-effort network proximity based on ICMP latency, clearly distinguished from physical distance.
- Temporary IPv4 speed limits and internet blocking through Npcap.
- Full shutdown cleanup for scans, capture sessions, tray resources, and ARP restoration.

Physical distance sensing requires Wi-Fi CSI hardware and calibration; the portable desktop build reports network proximity only.
