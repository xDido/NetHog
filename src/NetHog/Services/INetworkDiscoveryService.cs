using NetHog.Models;

namespace NetHog.Services;

public interface INetworkDiscoveryService
{
    IReadOnlyList<NetworkAdapterOption> GetAdapters();
    Task<NetworkSnapshot> ScanAsync(string? interfaceId = null, CancellationToken cancellationToken = default);
}
