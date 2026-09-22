using System.Net.Http.Headers;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace WifiBox.Services;

public sealed record AvailableUpdate(string Version, string Name, string ReleaseUrl);

/// <summary>
/// Checks the published release metadata in memory. No update cache or download file is created.
/// </summary>
public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/xDido/NetHog/releases/latest";
    private static readonly HttpClient Client = CreateClient();

    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(LatestReleaseUrl, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var tag = root.TryGetProperty("tag_name", out var tagProperty) ? tagProperty.GetString() : null;
        var url = root.TryGetProperty("html_url", out var urlProperty) ? urlProperty.GetString() : null;
        var name = root.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(url)) return null;

        var latest = ParseVersion(tag);
        var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        return latest > current
            ? new AvailableUpdate(tag.TrimStart('v', 'V'), name ?? $"NetHog {tag}", url)
            : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NetHog", "0.1"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static Version ParseVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var dash = normalized.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0) normalized = normalized[..dash];
        return Version.TryParse(normalized, out var version) ? version : new Version(0, 0, 0);
    }
}
