using System.Net;
using System.Xml.Linq;
using DependencyManager.Storage;

namespace DependencyManager.Services;

internal sealed class MavenCentralClient : IDisposable
{
    private const string BaseUrl = "https://repo.maven.apache.org/maven2";
    private static readonly TimeSpan MetadataCacheLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan PomCacheLifetime = TimeSpan.FromDays(7);
    private readonly HttpClient _httpClient;
    private readonly HttpCache _cache;
    private readonly Action<string, string>? _log;

    public MavenCentralClient(HttpCache cache, Action<string, string>? log = null)
    {
        _cache = cache;
        _log = log;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DependencyManager/0.2 (+read-only Gradle file analyzer)");
    }

    public async Task<IReadOnlyList<string>> GetVersionsAsync(string group, string artifact, CancellationToken cancellationToken)
    {
        ValidateCoordinatePart(group, nameof(group));
        ValidateCoordinatePart(artifact, nameof(artifact));
        string groupPath = string.Join('/', group.Split('.').Select(Uri.EscapeDataString));
        string relativePath = $"{groupPath}/{Uri.EscapeDataString(artifact)}/maven-metadata.xml";
        string url = $"{BaseUrl}/{relativePath}";
        string xml = await GetTextAsync(url, MetadataCacheLifetime, cancellationToken);
        XDocument document = XDocument.Parse(xml);
        return document.Descendants().Where(element => element.Name.LocalName == "version")
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0 &&
                !System.Text.RegularExpressions.Regex.IsMatch(value, @"(?i)(?:-SNAPSHOT|-M\d+|-RC\d+|-BETA\d*|-ALPHA\d*)$"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string> GetPomAsync(string group, string artifact, string version, CancellationToken cancellationToken)
    {
        ValidateCoordinatePart(group, nameof(group));
        ValidateCoordinatePart(artifact, nameof(artifact));
        ValidateCoordinatePart(version, nameof(version));
        string groupPath = string.Join('/', group.Split('.').Select(Uri.EscapeDataString));
        string escapedArtifact = Uri.EscapeDataString(artifact);
        string escapedVersion = Uri.EscapeDataString(version);
        string relativePath = $"{groupPath}/{escapedArtifact}/{escapedVersion}/{escapedArtifact}-{escapedVersion}.pom";
        return await GetTextAsync($"{BaseUrl}/{relativePath}", PomCacheLifetime, cancellationToken);
    }

    private async Task<string> GetTextAsync(string url, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        string? cached = await _cache.GetAsync(url, cancellationToken);
        if (cached is not null)
        {
            _log?.Invoke("CACHE", $"HIT {url}");
            return cached;
        }

        _log?.Invoke("HTTP", $"GET {url}");
        using HttpResponseMessage response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new FileNotFoundException($"Maven Central does not contain: {url}");
        response.EnsureSuccessStatusCode();
        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        await _cache.SetAsync(url, content, lifetime, cancellationToken);
        _log?.Invoke("HTTP", $"Cached {response.StatusCode} response ({content.Length:N0} characters).");
        return content;
    }

    private static void ValidateCoordinatePart(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !System.Text.RegularExpressions.Regex.IsMatch(value, @"^[A-Za-z0-9_.+\-]+$"))
            throw new InvalidDataException($"Invalid Maven coordinate part '{value}' ({parameterName}).");
    }

    public void Dispose() => _httpClient.Dispose();
}
