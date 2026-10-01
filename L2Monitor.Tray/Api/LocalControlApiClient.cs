using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using L2Monitor.Core.Api;
using L2Monitor.Tray.Bootstrap;

namespace L2Monitor.Tray.Api;

internal sealed class LocalControlApiClient
{
    private readonly HttpClient _httpClient;
    private readonly AgentBootstrapDiscovery _bootstrapDiscovery;
    private readonly object _bootstrapLock = new();
    private AgentBootstrapSnapshot? _cachedBootstrap;

    public LocalControlApiClient(HttpClient httpClient, AgentBootstrapDiscovery bootstrapDiscovery)
    {
        _httpClient = httpClient;
        _bootstrapDiscovery = bootstrapDiscovery;
    }

    public async Task<TrayDashboardSnapshot> GetDashboardAsync(CancellationToken cancellationToken)
    {
        var bootstrap = GetBootstrap();

        try
        {
            return await LoadDashboardAsync(bootstrap, cancellationToken).ConfigureAwait(false);
        }
        catch (AgentConnectionException) when (TryGetRediscoveredBootstrap(bootstrap, out var rediscoveredBootstrap))
        {
            return await LoadDashboardAsync(rediscoveredBootstrap, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<SettingsResponseDto> UpdateSettingsAsync(UpdateSettingsRequestDto request, CancellationToken cancellationToken)
        => await SendUsingCurrentBootstrapAsync<SettingsResponseDto>(
            HttpMethod.Put,
            "/v1/settings",
            request,
            cancellationToken,
            static response => response.ActiveLoopbackPort).ConfigureAwait(false);

    public async Task<SettingsResponseDto> UpdateTraySettingsAsync(UpdateTraySettingsRequestDto request, CancellationToken cancellationToken)
        => await SendUsingCurrentBootstrapAsync<SettingsResponseDto>(
            HttpMethod.Put,
            "/v1/settings/tray",
            request,
            cancellationToken,
            static response => response.ActiveLoopbackPort).ConfigureAwait(false);

    public async Task<SettingsResponseDto> UpdateTelegramSecretAsync(UpdateTelegramSecretRequestDto request, CancellationToken cancellationToken)
        => await SendUsingCurrentBootstrapAsync<SettingsResponseDto>(
            HttpMethod.Post,
            "/v1/settings/secrets/telegram",
            request,
            cancellationToken,
            static response => response.ActiveLoopbackPort).ConfigureAwait(false);

    public async Task<SettingsResponseDto> UpdateCloudSecretAsync(UpdateCloudSecretRequestDto request, CancellationToken cancellationToken)
        => await SendUsingCurrentBootstrapAsync<SettingsResponseDto>(
            HttpMethod.Post,
            "/v1/settings/secrets/cloud",
            request,
            cancellationToken,
            static response => response.ActiveLoopbackPort).ConfigureAwait(false);

    public async Task<SettingsResponseDto> GetSettingsAsync(CancellationToken cancellationToken)
        => await SendUsingCurrentBootstrapAsync<SettingsResponseDto>(
            HttpMethod.Get,
            "/v1/settings",
            null,
            cancellationToken,
            static response => response.ActiveLoopbackPort).ConfigureAwait(false);

    public Task<ActionResponseDto> TriggerTestNotificationAsync(CancellationToken cancellationToken) =>
        SendUsingCurrentBootstrapAsync<ActionResponseDto>(HttpMethod.Post, "/v1/actions/test-notification", null, cancellationToken);

    public Task<TelegramChatLinkSessionDto> StartTelegramChatLinkAsync(CancellationToken cancellationToken) =>
        SendUsingCurrentBootstrapAsync<TelegramChatLinkSessionDto>(HttpMethod.Post, "/v1/actions/telegram-link/start", null, cancellationToken);

    public Task<ActionResponseDto> ConfirmTelegramChatLinkAsync(CancellationToken cancellationToken) =>
        SendUsingCurrentBootstrapAsync<ActionResponseDto>(HttpMethod.Post, "/v1/actions/telegram-link/confirm", null, cancellationToken);

    public Task<ActionResponseDto> TriggerReloadAsync(CancellationToken cancellationToken) =>
        SendUsingCurrentBootstrapAsync<ActionResponseDto>(HttpMethod.Post, "/v1/actions/reload", null, cancellationToken);

    public Task<ActionResponseDto> TriggerRestartAsync(CancellationToken cancellationToken) =>
        SendUsingCurrentBootstrapAsync<ActionResponseDto>(HttpMethod.Post, "/v1/actions/restart-agent", null, cancellationToken);

    public Task<ActionResponseDto> TriggerShutdownAsync(CancellationToken cancellationToken) =>
        SendUsingCurrentBootstrapAsync<ActionResponseDto>(HttpMethod.Post, "/v1/actions/shutdown", null, cancellationToken);

    private Task<T> SendUsingCurrentBootstrapAsync<T>(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken cancellationToken,
        Func<T, int?>? activeLoopbackPortSelector = null) =>
        SendWithBootstrapFailoverAsync(GetBootstrap(), method, relativePath, body, cancellationToken, activeLoopbackPortSelector);

    private async Task<T> SendWithBootstrapFailoverAsync<T>(
        AgentBootstrapSnapshot bootstrap,
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken cancellationToken,
        Func<T, int?>? activeLoopbackPortSelector)
    {
        try
        {
            var response = await SendAsync<T>(bootstrap, method, relativePath, body, cancellationToken).ConfigureAwait(false);
            CacheBootstrap(bootstrap, activeLoopbackPortSelector?.Invoke(response));
            return response;
        }
        catch (AgentConnectionException) when (TryGetRediscoveredBootstrap(bootstrap, out var rediscoveredBootstrap))
        {
            var response = await SendAsync<T>(rediscoveredBootstrap, method, relativePath, body, cancellationToken).ConfigureAwait(false);
            CacheBootstrap(rediscoveredBootstrap, activeLoopbackPortSelector?.Invoke(response));
            return response;
        }
    }

    private async Task<T> SendAsync<T>(
        AgentBootstrapSnapshot bootstrap,
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(bootstrap.BaseAddress, relativePath));
        if (!string.IsNullOrWhiteSpace(bootstrap.LocalApiToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bootstrap.LocalApiToken);
        }

        request.Headers.Add(LocalControlApiContract.VersionHeaderName, LocalControlApiContract.CurrentVersion);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken).ConfigureAwait(false);
                return payload ?? throw new AgentConnectionException("The agent returned an empty response.");
            }

            var error = await TryReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            var message = error?.Message
                ?? $"The agent returned {(int)response.StatusCode} {response.ReasonPhrase}.";

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new AgentConnectionException($"Tray authentication failed: {message}");
            }

            throw new LocalControlApiException(response.StatusCode, error?.Error ?? "request_failed", message);
        }
        catch (LocalControlApiException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AgentConnectionException($"Unable to reach the local agent at {bootstrap.BaseAddress}. {ex.Message}", ex);
        }
    }

    private async Task<TrayDashboardSnapshot> LoadDashboardAsync(
        AgentBootstrapSnapshot bootstrap,
        CancellationToken cancellationToken)
    {
        var statusTask = SendAsync<StatusResponseDto>(bootstrap, HttpMethod.Get, "/v1/status", null, cancellationToken);
        var settingsTask = SendAsync<SettingsResponseDto>(bootstrap, HttpMethod.Get, "/v1/settings", null, cancellationToken);
        var incidentsTask = SendAsync<IncidentsResponseDto>(bootstrap, HttpMethod.Get, "/v1/incidents?limit=10", null, cancellationToken);

        await Task.WhenAll(statusTask, settingsTask, incidentsTask).ConfigureAwait(false);

        var status = await statusTask.ConfigureAwait(false);
        var settings = await settingsTask.ConfigureAwait(false);
        CacheBootstrap(bootstrap, settings.ActiveLoopbackPort);

        return new TrayDashboardSnapshot(
            GetBootstrap(),
            status,
            settings,
            await incidentsTask.ConfigureAwait(false));
    }

    private static async Task<ErrorResponseDto?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<ErrorResponseDto>(cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private AgentBootstrapSnapshot GetBootstrap()
    {
        lock (_bootstrapLock)
        {
            _cachedBootstrap ??= _bootstrapDiscovery.Load();
            return _cachedBootstrap;
        }
    }

    private void CacheBootstrap(AgentBootstrapSnapshot bootstrap, int? activeLoopbackPort = null)
    {
        lock (_bootstrapLock)
        {
            _cachedBootstrap = activeLoopbackPort is > 0 and <= 65535
                ? bootstrap with { BaseAddress = CreateLoopbackBaseAddress(activeLoopbackPort.Value) }
                : bootstrap;
        }
    }

    private bool TryGetRediscoveredBootstrap(AgentBootstrapSnapshot failedBootstrap, out AgentBootstrapSnapshot rediscoveredBootstrap)
    {
        rediscoveredBootstrap = _bootstrapDiscovery.Load();
        if (rediscoveredBootstrap.BaseAddress == failedBootstrap.BaseAddress
            && string.Equals(rediscoveredBootstrap.LocalApiToken, failedBootstrap.LocalApiToken, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static Uri CreateLoopbackBaseAddress(int port) =>
        new($"http://127.0.0.1:{port}/", UriKind.Absolute);
}

internal sealed record TrayDashboardSnapshot(
    AgentBootstrapSnapshot Bootstrap,
    StatusResponseDto Status,
    SettingsResponseDto Settings,
    IncidentsResponseDto Incidents);

internal sealed class LocalControlApiException : Exception
{
    public LocalControlApiException(HttpStatusCode statusCode, string errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }
    public string ErrorCode { get; }
}

internal sealed class AgentConnectionException : Exception
{
    public AgentConnectionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
