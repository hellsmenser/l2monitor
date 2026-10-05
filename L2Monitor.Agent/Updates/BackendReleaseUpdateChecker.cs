using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace L2Monitor.Agent.Updates;

internal interface IAgentReleaseUpdateChecker
{
    Task<AgentUpdateCheckResult> CheckAsync(
        Uri backendBaseUri,
        string currentVersion,
        CancellationToken cancellationToken);
}

internal sealed class BackendReleaseUpdateChecker(
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<BackendReleaseUpdateChecker> logger) : IAgentReleaseUpdateChecker
{
    internal const string HttpClientName = "backend-release-update";
    private const int MaxResponseBytes = 64 * 1024;

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<BackendReleaseUpdateChecker> _logger = logger;

    public async Task<AgentUpdateCheckResult> CheckAsync(
        Uri backendBaseUri,
        string currentVersion,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var normalizedCurrent = NormalizeVersionForDisplay(currentVersion) ?? "0.0.0";
        var endpoint = new Uri(backendBaseUri, "/public/client-version");
        var client = _httpClientFactory.CreateClient(HttpClientName);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Backend version check failed. StatusCode={StatusCode}",
                    (int)response.StatusCode);
                return Unreachable(normalizedCurrent, now);
            }

            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                return Invalid(normalizedCurrent, now);
            }

            var payload = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!TryGetRequiredString(root, "version", out var versionValue)
                || NormalizeVersionForDisplay(versionValue) is not { } latestVersion
                || !root.TryGetProperty("required", out var requiredElement)
                || requiredElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !TryReadDownloadUrl(root, out var releaseUrl))
            {
                return Invalid(normalizedCurrent, now);
            }

            var available = IsNewerStableVersion(latestVersion, normalizedCurrent);
            return new AgentUpdateCheckResult(
                available ? "available" : "current",
                normalizedCurrent,
                latestVersion,
                available,
                available && requiredElement.GetBoolean(),
                available ? releaseUrl?.AbsoluteUri : null,
                now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            _logger.LogWarning(
                "Backend version check failed. ExceptionType={ExceptionType}",
                ex.GetType().Name);
            return Unreachable(normalizedCurrent, now);
        }
    }

    internal static bool IsNewerStableVersion(string latest, string current) =>
        TryParseStableVersion(latest, out var latestVersion)
        && TryParseStableVersion(current, out var currentVersion)
        && latestVersion > currentVersion;

    internal static string? NormalizeVersionForDisplay(string? value)
    {
        if (!TryParseStableVersion(value, out var version))
        {
            return null;
        }

        return version.Revision > 0
            ? $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private static bool TryParseStableVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var metadataIndex = normalized.IndexOf('+');
        if (metadataIndex >= 0)
        {
            normalized = normalized[..metadataIndex];
        }
        if (normalized.Contains('-'))
        {
            return false;
        }

        var parts = normalized.Split('.');
        if (parts.Length is < 2 or > 4
            || parts.Any(static part => !int.TryParse(part, out var number) || number < 0))
        {
            return false;
        }

        var numbers = parts.Select(int.Parse).ToArray();
        version = numbers.Length switch
        {
            2 => new Version(numbers[0], numbers[1], 0, 0),
            3 => new Version(numbers[0], numbers[1], numbers[2], 0),
            4 => new Version(numbers[0], numbers[1], numbers[2], numbers[3]),
            _ => version,
        };
        return true;
    }

    private static bool TryReadDownloadUrl(JsonElement root, out Uri? releaseUrl)
    {
        releaseUrl = null;
        if (!root.TryGetProperty("download_url", out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate.Scheme != Uri.UriSchemeHttps
            || !candidate.IsDefaultPort
            || !string.IsNullOrEmpty(candidate.UserInfo))
        {
            return false;
        }

        releaseUrl = candidate;
        return true;
    }

    private static bool TryGetRequiredString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        return root.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = element.GetString()!);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (buffer.Length <= MaxResponseBytes)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.ToArray();
            }
            buffer.Write(chunk, 0, read);
        }

        throw new InvalidDataException("Backend version response exceeds the allowed size.");
    }

    private static AgentUpdateCheckResult Invalid(string currentVersion, DateTimeOffset checkedAtUtc) =>
        new("invalid_response", currentVersion, null, false, false, null, checkedAtUtc);

    private static AgentUpdateCheckResult Unreachable(string currentVersion, DateTimeOffset checkedAtUtc) =>
        new("unreachable", currentVersion, null, false, false, null, checkedAtUtc);
}

internal sealed record AgentUpdateCheckResult(
    string State,
    string CurrentVersion,
    string? LatestVersion,
    bool IsUpdateAvailable,
    bool Required,
    string? ReleaseUrl,
    DateTimeOffset CheckedAtUtc);
