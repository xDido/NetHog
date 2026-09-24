using System.IO;
using System.Text.Json;
using NetHog.Models;

namespace NetHog.Services;

/// <summary>
/// Keeps the small amount of domain metadata collected during the current
/// session and persists only the user's explicit block rules.
/// </summary>
public sealed class DomainObservationStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ObservationState> _observations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _blockedDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _rulesPath;

    public DomainObservationStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetHog");
        Directory.CreateDirectory(directory);
        _rulesPath = Path.Combine(directory, "domain-rules.json");
        LoadRules();
    }

    public void Observe(string deviceMacAddress, string deviceDisplayName, string domain)
    {
        var mac = NormalizeMac(deviceMacAddress);
        var normalizedDomain = NormalizeDomain(domain);
        if (mac.Length == 0 || normalizedDomain.Length == 0) return;

        lock (_sync)
        {
            var key = CreateKey(mac, normalizedDomain);
            if (_observations.TryGetValue(key, out var existing))
            {
                existing.LastSeen = DateTimeOffset.UtcNow;
                existing.SeenCount++;
                return;
            }

            _observations[key] = new ObservationState
            {
                DeviceMacAddress = mac,
                DeviceDisplayName = string.IsNullOrWhiteSpace(deviceDisplayName) ? "Unknown device" : deviceDisplayName,
                Domain = normalizedDomain,
                LastSeen = DateTimeOffset.UtcNow,
                SeenCount = 1
            };
        }
    }

    public IReadOnlyList<DomainObservation> GetObservations()
    {
        lock (_sync)
        {
            return _observations.Values
                .OrderByDescending(item => item.LastSeen)
                .Select(item => new DomainObservation(
                    item.DeviceMacAddress,
                    item.DeviceDisplayName,
                    item.Domain,
                    item.LastSeen,
                    item.SeenCount,
                    IsBlockedUnsafe(item.DeviceMacAddress, item.Domain)))
                .ToArray();
        }
    }

    public void ClearObservations()
    {
        lock (_sync) _observations.Clear();
    }

    public bool IsBlocked(string deviceMacAddress, string domain)
    {
        var mac = NormalizeMac(deviceMacAddress);
        var normalizedDomain = NormalizeDomain(domain);
        if (mac.Length == 0 || normalizedDomain.Length == 0) return false;

        lock (_sync) return IsBlockedUnsafe(mac, normalizedDomain);
    }

    public void SetBlocked(string deviceMacAddress, string domain, bool blocked)
    {
        var mac = NormalizeMac(deviceMacAddress);
        var normalizedDomain = NormalizeDomain(domain);
        if (mac.Length == 0 || normalizedDomain.Length == 0) return;

        lock (_sync)
        {
            if (blocked)
            {
                if (!_blockedDomains.TryGetValue(mac, out var domains))
                {
                    domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _blockedDomains[mac] = domains;
                }

                domains.Add(normalizedDomain);
            }
            else if (_blockedDomains.TryGetValue(mac, out var existing))
            {
                existing.Remove(normalizedDomain);
                if (existing.Count == 0) _blockedDomains.Remove(mac);
            }

            SaveRulesUnsafe();
        }
    }

    public static string NormalizeDomain(string value)
    {
        var normalized = value.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.StartsWith("www.", StringComparison.Ordinal)) normalized = normalized[4..];
        return normalized.Length <= 253 && normalized.All(character =>
            char.IsLetterOrDigit(character) || character is '.' or '-' or '_')
            ? normalized
            : string.Empty;
    }

    private bool IsBlockedUnsafe(string mac, string domain)
    {
        if (!_blockedDomains.TryGetValue(mac, out var blockedDomains)) return false;
        return blockedDomains.Any(rule =>
            domain.Equals(rule, StringComparison.OrdinalIgnoreCase)
            || domain.EndsWith('.' + rule, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadRules()
    {
        try
        {
            if (!File.Exists(_rulesPath)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, string[]>>(
                File.ReadAllText(_rulesPath));
            if (saved is null) return;

            foreach (var entry in saved)
            {
                var mac = NormalizeMac(entry.Key);
                if (mac.Length == 0) continue;
                var domains = entry.Value
                    .Select(NormalizeDomain)
                    .Where(domain => domain.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (domains.Count > 0) _blockedDomains[mac] = domains;
            }
        }
        catch
        {
            // A damaged optional rules file should not prevent the app starting.
        }
    }

    private void SaveRulesUnsafe()
    {
        try
        {
            var temporaryPath = _rulesPath + ".tmp";
            var snapshot = _blockedDomains.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.OrderBy(domain => domain).ToArray(),
                StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
            File.Move(temporaryPath, _rulesPath, true);
        }
        catch
        {
            // Blocking still applies for this process even if persistence is unavailable.
        }
    }

    private static string CreateKey(string mac, string domain) => mac + "\0" + domain;

    private static string NormalizeMac(string value)
    {
        var parts = value.Split(new[] { ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 6 || parts.Any(part => !byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out _)))
        {
            return string.Empty;
        }

        return string.Join(':', parts.Select(part => Convert.ToByte(part, 16).ToString("X2")));
    }

    private sealed class ObservationState
    {
        public string DeviceMacAddress { get; init; } = string.Empty;
        public string DeviceDisplayName { get; init; } = string.Empty;
        public string Domain { get; init; } = string.Empty;
        public DateTimeOffset LastSeen { get; set; }
        public int SeenCount { get; set; }
    }
}
