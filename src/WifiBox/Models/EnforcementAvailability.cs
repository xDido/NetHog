namespace WifiBox.Models;

public sealed record EnforcementAvailability(
    bool IsAvailable,
    string Status,
    string Explanation,
    bool IsAdministrator = false,
    bool IsNpcapAvailable = false);
