using NetHog.Models;

namespace NetHog.Services;

/// <summary>
/// Controls a temporary IPv4 packet-forwarding session on the selected network adapter.
/// </summary>
public interface IEnforcementService
{
    Task<EnforcementAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default);
    Task StartSessionAsync(NetworkSnapshot network, CancellationToken cancellationToken = default);
    Task StopSessionAsync(CancellationToken cancellationToken = default);
    bool IsSessionActive { get; }
    Task ApplyRuleAsync(DeviceControlRule rule, CancellationToken cancellationToken = default);
    Task RemoveRuleAsync(string deviceMacAddress, CancellationToken cancellationToken = default);
    IReadOnlyList<DomainObservation> GetDomainObservations();
    void SetDomainBlocked(string deviceMacAddress, string domain, bool blocked);
}
