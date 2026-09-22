using WifiBox.Models;

namespace WifiBox.Services;

public interface INetworkDiscoveryService
{
    Task<NetworkSnapshot> ScanAsync(CancellationToken cancellationToken = default);
}
