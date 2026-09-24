using System.Net.Http.Headers;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetHog.Services;

public sealed record AvailableUpdate(string Version, string Name, string ReleaseUrl);

/// <summary>
/// Checks the published release metadata in memory. No update cache or download file is created.
/// </summary>
public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/xDido/NetHog/releases/latest";
    private const string UpdatePublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAr6lm5BsU7QF0AVrLEtVA
        zzh8vnif1XlcyqWDQA5UfTD0brfm10JJMwqnhFn5Q4V4gniCe6OxK5B9ASSUGZrT
        dPy8lnC59SFi6mHZ+PT7CZjmtq7rdcS6/lEhOZf0VS61OIOsLRo8UBIkv7FoDdM4
        J7aCBCb0R9haE/fZ8LyjUKylsSyLOlRlf5PQFrftvX//D/c7/IONgRRq6/pdGKp2
        jIudm5MKfnoPW1SRCN7N6dpmUHV/tJhhRLfHEjhplnTDM6da2M9vO1/co01YB75w
        c3v7Zm5wRhADhS/SkzOmhN1RwxSDVqPtNj0jpldX5FfsYheYcPVhe4/7rNbRwQOM
        85pJ5dKW7xG/sqZZolCp063HLA8ltkLKOmpqG/4uGVY/iGl1Jdull27uKuc/jHLs
        dsW5KbbdzbQbL/7EM55xQcCukxBMoR76qfCfr4X3MjyavXy4REDAYo/qRllrqamL
        oXGoe5EH9o9RLkbn0NjwvHqxDJY8vcav4GXVP3DZG8f5AgMBAAE=
        -----END PUBLIC KEY-----
        """;
    private static readonly HttpClient Client = CreateClient(TimeSpan.FromSeconds(8));
    private static readonly HttpClient AssetClient = CreateClient(TimeSpan.FromMinutes(5));

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
        if (latest <= current) return null;

        var version = tag.Trim().TrimStart('v', 'V');
        var archiveName = $"release-v{version}.zip";
        var signatureName = $"{archiveName}.sig";
        var archiveUrl = FindAssetUrl(root, archiveName);
        var signatureUrl = FindAssetUrl(root, signatureName);
        if (archiveUrl is null || signatureUrl is null) return null;

        var signatureText = await Client.GetStringAsync(signatureUrl, cancellationToken);
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureText.Trim());
        }
        catch (FormatException)
        {
            return null;
        }

        using var archiveResponse = await AssetClient.GetAsync(
            archiveUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!archiveResponse.IsSuccessStatusCode) return null;

        await using var archiveStream = await archiveResponse.Content.ReadAsStreamAsync(cancellationToken);
        var archiveHash = await SHA256.HashDataAsync(archiveStream, cancellationToken);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(UpdatePublicKeyPem);
        if (!rsa.VerifyHash(archiveHash, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            return null;
        }

        return new AvailableUpdate(version, name ?? $"NetHog {tag}", url);
    }

    private static string? FindAssetUrl(JsonElement release, string fileName)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
            var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProperty)
                ? urlProperty.GetString()
                : null;
            if (string.Equals(name, fileName, StringComparison.Ordinal) &&
                Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps)
            {
                return uri.AbsoluteUri;
            }
        }

        return null;
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
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
