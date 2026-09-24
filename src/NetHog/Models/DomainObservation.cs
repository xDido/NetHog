namespace NetHog.Models;

public sealed record DomainObservation(
    string DeviceMacAddress,
    string DeviceDisplayName,
    string Domain,
    DateTimeOffset LastSeen,
    int SeenCount,
    bool IsBlocked);
