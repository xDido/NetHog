namespace NetHog.Models;

public sealed record NetworkAdapterOption(
    string Id,
    string Name,
    string Description,
    string LocalAddress,
    string GatewayAddress,
    bool IsDefaultRoute)
{
    public string DisplayName => IsDefaultRoute
        ? $"{Name} · {LocalAddress} · default route"
        : $"{Name} · {LocalAddress}";

    public string DiagnosticSummary =>
        $"{Description} · gateway {GatewayAddress}" +
        (IsDefaultRoute ? " · selected by Windows default route" : string.Empty);
}
