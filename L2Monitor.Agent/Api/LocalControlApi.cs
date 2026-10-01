using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using L2Monitor.Agent.Hosting;
using L2Monitor.Agent.Runtime;
using L2Monitor.Core.Api;
using L2Monitor.Core.Delivery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace L2Monitor.Agent;

internal static class LocalControlApi
{
    public static WebApplication MapLocalControlApi(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            context.Response.Headers[LocalControlApiContract.VersionHeaderName] = LocalControlApiContract.CurrentVersion;
            context.Response.Headers[LocalControlApiContract.MinimumVersionHeaderName] = LocalControlApiContract.MinimumSupportedVersion;

            if (!context.Request.Path.StartsWithSegments("/v1", StringComparison.Ordinal))
            {
                await next().ConfigureAwait(false);
                return;
            }

            if (!IsVersionSupported(context.Request.Headers[LocalControlApiContract.VersionHeaderName]))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(
                    new ErrorResponseDto(
                        CreateVersionDto(),
                        "unsupported_api_version",
                        "Requested API version is not supported by this agent.")).ConfigureAwait(false);
                return;
            }

            var configurationService = context.RequestServices.GetRequiredService<AgentConfigurationService>();
            if (!IsAuthorized(context.Request, configurationService)
                && !IsRecoveryRequestAllowed(context.Request, configurationService))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(
                    new ErrorResponseDto(
                        CreateVersionDto(),
                        "unauthorized",
                        "A valid local bearer token is required.")).ConfigureAwait(false);
                return;
            }

            await next().ConfigureAwait(false);
        });

        var v1 = app.MapGroup("/v1");

        v1.MapGet("/status", (HttpContext context, AgentCommandService commands) => Results.Ok(commands.GetStatus(GetActiveLoopbackPort(context))));
        v1.MapGet("/connections", (AgentCommandService commands) => Results.Ok(commands.GetConnections()));
        v1.MapGet("/incidents", (int? limit, AgentCommandService commands) => Results.Ok(commands.GetIncidents(limit ?? 50)));
        v1.MapGet("/settings", (HttpContext context, AgentCommandService commands) => Results.Ok(commands.GetSettings(GetActiveLoopbackPort(context))));
        v1.MapPut("/settings", (HttpContext context, UpdateSettingsRequestDto request, AgentCommandService commands) =>
        {
            try
            {
                return Results.Ok(commands.UpdateSettings(request, GetActiveLoopbackPort(context)));
            }
            catch (AgentSettingsValidationException ex)
            {
                return Results.BadRequest(new ErrorResponseDto(
                    CreateVersionDto(),
                    "invalid_settings",
                    ex.Message));
            }
        });
        v1.MapPut("/settings/tray", (HttpContext context, UpdateTraySettingsRequestDto request, AgentCommandService commands) =>
        {
            try
            {
                return Results.Ok(commands.UpdateTraySettings(request, GetActiveLoopbackPort(context)));
            }
            catch (AgentSettingsValidationException ex)
            {
                return Results.BadRequest(new ErrorResponseDto(
                    CreateVersionDto(),
                    "invalid_settings",
                    ex.Message));
            }
        });
        v1.MapPost("/settings/secrets/telegram", (HttpContext context, UpdateTelegramSecretRequestDto request, AgentCommandService commands) => Results.Ok(commands.UpdateTelegramSecret(request, GetActiveLoopbackPort(context))));
        v1.MapPost("/settings/secrets/cloud", (HttpContext context, UpdateCloudSecretRequestDto request, AgentCommandService commands) => Results.Ok(commands.UpdateCloudSecret(request, GetActiveLoopbackPort(context))));
        v1.MapPost("/actions/telegram-link/start", async (AgentCommandService commands) => Results.Ok(await commands.StartTelegramChatLinkAsync().ConfigureAwait(false)));
        v1.MapPost("/actions/telegram-link/confirm", async (AgentCommandService commands) => Results.Ok(await commands.ConfirmTelegramChatLinkAsync().ConfigureAwait(false)));
        v1.MapPost("/actions/test-notification", async (AgentCommandService commands) => Results.Ok(await commands.TestNotificationAsync().ConfigureAwait(false)));
        v1.MapPost("/actions/reload", (HttpContext context, AgentCommandService commands) => Results.Ok(commands.Reload(GetActiveLoopbackPort(context))));
        v1.MapPost("/actions/restart-agent", (HttpContext context, AgentCommandService commands, IHostApplicationLifetime lifetime) =>
        {
            var restart = commands.Restart();
            if (restart.ShouldStop)
            {
                context.Response.OnCompleted(() =>
                {
                    lifetime.StopApplication();
                    return Task.CompletedTask;
                });
            }

            return Results.Ok(restart.Response);
        });
        v1.MapPost("/actions/shutdown", (HttpContext context, IHostApplicationLifetime lifetime, TimeProvider timeProvider) =>
        {
            context.Response.OnCompleted(() =>
            {
                lifetime.StopApplication();
                return Task.CompletedTask;
            });

            return Results.Ok(new ActionResponseDto(
                CreateVersionDto(),
                new AgentActionResultDto(
                    "shutdown",
                    "accepted",
                    "Aden+ agent shutdown has been requested.",
                    timeProvider.GetUtcNow())));
        });

        return app;
    }

    private static bool IsVersionSupported(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue))
        {
            return true;
        }

        var value = headerValue.Trim();
        var majorPart = value.Split('.', 2)[0];
        return int.TryParse(majorPart, out var major) && major == 1;
    }

    private static bool IsAuthorized(HttpRequest request, AgentConfigurationService configurationService)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var providedToken = header["Bearer ".Length..].Trim();
        var expectedToken = configurationService.GetSnapshot().Secrets.LocalApiToken;
        if (string.IsNullOrWhiteSpace(providedToken) || string.IsNullOrWhiteSpace(expectedToken))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(providedToken);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        return providedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    private static ApiVersionDto CreateVersionDto() =>
        new(LocalControlApiContract.CurrentVersion, LocalControlApiContract.MinimumSupportedVersion);

    private static int GetActiveLoopbackPort(HttpContext context)
    {
        if (context.Connection.LocalPort > 0)
        {
            return context.Connection.LocalPort;
        }

        if (context.Request.Host.Port is > 0 and var hostPort)
        {
            return hostPort;
        }

        return string.Equals(context.Request.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            ? 443
            : 80;
    }

    private static bool IsRecoveryRequestAllowed(HttpRequest request, AgentConfigurationService configurationService)
    {
        if (!configurationService.IsRecoveryModeActive())
        {
            return false;
        }

        var path = request.Path;
        return request.Method switch
        {
            "GET" when path.Equals("/v1/status", StringComparison.Ordinal)
                || path.Equals("/v1/settings", StringComparison.Ordinal)
                || path.Equals("/v1/incidents", StringComparison.Ordinal) => true,
            "POST" when path.Equals("/v1/settings/secrets/telegram", StringComparison.Ordinal)
                || path.Equals("/v1/settings/secrets/cloud", StringComparison.Ordinal) => true,
            _ => false,
        };
    }
}

internal sealed class AgentCommandService
{
    private static readonly TimeSpan TelegramLinkCodeLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TelegramLinkRecencySlack = TimeSpan.FromSeconds(30);
    private readonly AgentConfigurationService _configuration;
    private readonly AgentRuntimeStateStore _runtimeState;
    private readonly AgentControlStateStore _controlState;
    private readonly IAgentNotificationSender _notificationSender;
    private readonly IAgentRestartCoordinator _restartCoordinator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentCommandService> _logger;
    private readonly object _telegramLinkSync = new();
    private PendingTelegramChatLink? _pendingTelegramChatLink;

    public AgentCommandService(
        AgentConfigurationService configuration,
        AgentRuntimeStateStore runtimeState,
        AgentControlStateStore controlState,
        IAgentNotificationSender notificationSender,
        IAgentRestartCoordinator restartCoordinator,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger<AgentCommandService> logger)
    {
        _configuration = configuration;
        _runtimeState = runtimeState;
        _controlState = controlState;
        _notificationSender = notificationSender;
        _restartCoordinator = restartCoordinator;
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public StatusResponseDto GetStatus(int activeLoopbackPort)
    {
        var config = _configuration.GetSnapshot();
        var runtime = _runtimeState.GetSnapshot();
        var connections = runtime.Connections.Select(MapConnection).ToArray();

        return new StatusResponseDto(
            CreateVersionDto(),
            runtime.Status.ToString(),
            config.Settings.Delivery.Mode,
            activeLoopbackPort,
            runtime.LastObservedConnectionCount,
            runtime.ProbeCount,
            runtime.StartedAtUtc,
            runtime.LastProbeCompletedAtUtc,
            _controlState.GetLastDeliveryHealth() is { } deliveryHealth
                ? MapDeliveryHealth(deliveryHealth)
                : BuildDeliveryHealth(config),
            _controlState.GetLastBackendHealth() is { } lastBackend
                ? MapHealth(lastBackend)
                : BuildBackendHealth(config),
            _controlState.GetLastIncident() is { } incident ? MapIncident(incident) : null,
            _controlState.GetLastNotificationResult() is { } notification ? MapActionResult(notification) : null,
            connections);
    }

    public ConnectionsResponseDto GetConnections() =>
        new(CreateVersionDto(), _runtimeState.GetSnapshot().Connections.Select(MapConnection).ToArray());

    public IncidentsResponseDto GetIncidents(int limit) =>
        new(CreateVersionDto(), _controlState.GetIncidents(limit).Select(MapIncident).ToArray());

    public SettingsResponseDto GetSettings(int activeLoopbackPort)
    {
        var snapshot = _configuration.GetSnapshot();
        return new SettingsResponseDto(
            CreateVersionDto(),
            activeLoopbackPort,
            MapSettings(snapshot.Settings),
            BuildSecretPresence(snapshot.Secrets),
            BuildPendingTelegramChatLinkDto(_timeProvider.GetUtcNow(), snapshot));
    }

    public SettingsResponseDto UpdateSettings(UpdateSettingsRequestDto request, int activeLoopbackPort)
    {
        var snapshot = _configuration.UpdateSettings(request.Settings);
        _configuration.RefreshBootstrapSnapshot(activeLoopbackPort);
        var now = _timeProvider.GetUtcNow();
        _controlState.InvalidateCachedHealth();
        _controlState.RecordIncident("settings_updated", "info", "Agent settings were updated over the loopback API.", now);
        PublishConfigurationDiagnostics("settings update", now);
        return new SettingsResponseDto(
            CreateVersionDto(),
            activeLoopbackPort,
            MapSettings(snapshot.Settings),
            BuildSecretPresence(snapshot.Secrets),
            BuildPendingTelegramChatLinkDto(now, snapshot));
    }

    public SettingsResponseDto UpdateTraySettings(UpdateTraySettingsRequestDto request, int activeLoopbackPort)
    {
        var snapshot = _configuration.UpdateTraySettings(request.Settings);
        _configuration.RefreshBootstrapSnapshot(activeLoopbackPort);
        var now = _timeProvider.GetUtcNow();
        _controlState.InvalidateCachedHealth();
        _controlState.RecordIncident("tray_settings_updated", "info", "Tray-editable settings were updated over the loopback API.", now);
        PublishConfigurationDiagnostics("tray settings update", now);
        return new SettingsResponseDto(
            CreateVersionDto(),
            activeLoopbackPort,
            MapSettings(snapshot.Settings),
            BuildSecretPresence(snapshot.Secrets),
            BuildPendingTelegramChatLinkDto(now, snapshot));
    }

    public SettingsResponseDto UpdateTelegramSecret(UpdateTelegramSecretRequestDto request, int activeLoopbackPort)
    {
        var snapshot = _configuration.UpdateTelegramSecret(request.BotToken);
        _configuration.RefreshBootstrapSnapshot(activeLoopbackPort);
        _controlState.InvalidateCachedHealth();
        _controlState.RecordIncident("telegram_secret_updated", "info", "Telegram secret was updated.", _timeProvider.GetUtcNow());
        return new SettingsResponseDto(
            CreateVersionDto(),
            activeLoopbackPort,
            MapSettings(snapshot.Settings),
            BuildSecretPresence(snapshot.Secrets),
            BuildPendingTelegramChatLinkDto(_timeProvider.GetUtcNow(), snapshot));
    }

    public SettingsResponseDto UpdateCloudSecret(UpdateCloudSecretRequestDto request, int activeLoopbackPort)
    {
        var snapshot = _configuration.UpdateCloudSecret(request.AuthKey);
        _configuration.RefreshBootstrapSnapshot(activeLoopbackPort);
        _controlState.InvalidateCachedHealth();
        _controlState.RecordIncident("cloud_secret_updated", "info", "Cloud secret was updated.", _timeProvider.GetUtcNow());
        return new SettingsResponseDto(
            CreateVersionDto(),
            activeLoopbackPort,
            MapSettings(snapshot.Settings),
            BuildSecretPresence(snapshot.Secrets),
            BuildPendingTelegramChatLinkDto(_timeProvider.GetUtcNow(), snapshot));
    }
    public Task<TelegramChatLinkSessionDto> StartTelegramChatLinkAsync()
    {
        var snapshot = _configuration.GetSnapshot();
        var now = _timeProvider.GetUtcNow();

        if (string.IsNullOrWhiteSpace(snapshot.Secrets.TelegramBotToken))
        {
            return Task.FromResult(new TelegramChatLinkSessionDto(
                CreateVersionDto(),
                "rejected",
                "Сначала сохраните токен Telegram. Без него код привязки не сработает.",
                null,
                null,
                BuildDeliveryHealth(snapshot)));
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var expiresAtUtc = now.Add(TelegramLinkCodeLifetime);

        lock (_telegramLinkSync)
        {
            _pendingTelegramChatLink = new PendingTelegramChatLink(code, now, expiresAtUtc);
        }

        return Task.FromResult(new TelegramChatLinkSessionDto(
            CreateVersionDto(),
            "pending",
            "Отправьте этот код боту в личном чате, затем нажмите «Подтвердить привязку».",
            code,
            expiresAtUtc,
            BuildDeliveryHealth(snapshot)));
    }

    public async Task<ActionResponseDto> ConfirmTelegramChatLinkAsync()
    {
        var snapshot = _configuration.GetSnapshot();
        var now = _timeProvider.GetUtcNow();

        if (string.IsNullOrWhiteSpace(snapshot.Secrets.TelegramBotToken))
        {
            return new ActionResponseDto(
                CreateVersionDto(),
                new AgentActionResultDto(
                    "confirm-telegram-chat-link",
                    "rejected",
                    "Сначала сохраните токен Telegram, потом запросите код привязки.",
                    now),
                Delivery: BuildDeliveryHealth(snapshot));
        }

        var pendingLink = GetPendingTelegramChatLink(now);
        if (pendingLink is null)
        {
            return new ActionResponseDto(
                CreateVersionDto(),
                new AgentActionResultDto(
                    "confirm-telegram-chat-link",
                    "rejected",
                    "Код привязки не найден или уже истёк. Сначала нажмите «Получить код» заново.",
                    now),
                Delivery: BuildDeliveryHealth(snapshot));
        }

        var bindingAttempt = await TryResolvePrivateTelegramChatIdByCodeAsync(
            snapshot.Secrets.TelegramBotToken,
            pendingLink,
            now).ConfigureAwait(false);
        if (!bindingAttempt.Success)
        {
            return new ActionResponseDto(
                CreateVersionDto(),
                new AgentActionResultDto(
                    "confirm-telegram-chat-link",
                    "failed",
                    bindingAttempt.Message,
                    now),
                Delivery: BuildTelegramBindingDeliveryHealth(snapshot, bindingAttempt));
        }

        snapshot = _configuration.UpdateTelegramChatId(bindingAttempt.ChatId!.Value);
        ClearPendingTelegramChatLink();
        _controlState.InvalidateCachedHealth();
        _controlState.RecordIncident("telegram_chat_linked", "info", $"Telegram chat binding updated for chat {bindingAttempt.ChatId.Value}.", now);

        return new ActionResponseDto(
            CreateVersionDto(),
            new AgentActionResultDto(
                "confirm-telegram-chat-link",
                "completed",
                "Чат привязан по коду подтверждения.",
                now),
            Delivery: BuildDeliveryHealth(snapshot));
    }

    public async Task<ActionResponseDto> TestNotificationAsync()
    {
        var config = _configuration.GetSnapshot();
        var dispatch = await _notificationSender.SendTestNotificationAsync(config).ConfigureAwait(false);
        var result = new AgentActionResultRecord(
            "test-notification",
            MapNotificationOutcome(dispatch.Health.State, dispatch.Delivered),
            dispatch.Health.Summary,
            dispatch.Health.CheckedAtUtc ?? _timeProvider.GetUtcNow());

        _controlState.SetLastNotificationResult(result);
        _controlState.SetLastDeliveryHealth(dispatch.Health);
        RecordDeliveryIncident(dispatch.Health);
        return new ActionResponseDto(CreateVersionDto(), MapActionResult(result), MapDeliveryHealth(dispatch.Health));
    }

    private async Task<TelegramChatBindingResult> TryResolvePrivateTelegramChatIdByCodeAsync(string botToken, PendingTelegramChatLink pendingLink, DateTimeOffset now)
    {
        var client = _httpClientFactory.CreateClient(AgentNotificationSender.TelegramHttpClientName);
        var code = pendingLink.Code;

        try
        {
            using var response = await client.GetAsync(
                $"https://api.telegram.org/bot{botToken}/getUpdates",
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return CreateTelegramHttpFailureResult(response.StatusCode, response.ReasonPhrase);
            }

            var payload = await response.Content.ReadFromJsonAsync<TelegramGetUpdatesResponse>().ConfigureAwait(false);
            if (payload is null)
            {
                return new TelegramChatBindingResult(
                    null,
                    false,
                    "Telegram вернул пустой ответ при подтверждении привязки.");
            }

            if (!payload.Ok)
            {
                return CreateTelegramPayloadFailureResult(payload.Description);
            }

            var confirmedMessage = payload.Result?
                .SelectMany(static update => new[]
                {
                    new TelegramUpdateMessageDto(update.UpdateId, update.Message),
                    new TelegramUpdateMessageDto(update.UpdateId, update.EditedMessage)
                })
                .Where(item => item.Message?.Chat is not null
                    && string.Equals(item.Message.Chat.Type, "private", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(item.Message.Text?.Trim(), pendingLink.Code, StringComparison.Ordinal)
                    && item.Message.Date >= pendingLink.IssuedAtUtc.Subtract(TelegramLinkRecencySlack).ToUnixTimeSeconds())
                .OrderByDescending(static item => item.UpdateId)
                .ThenByDescending(static item => item.Message!.Date)
                .ThenByDescending(static item => item.Message!.Chat!.Id)
                .FirstOrDefault();

            if (confirmedMessage?.Message?.Chat is null)
            {
                return new TelegramChatBindingResult(
                    null,
                    false,
                    $"Не нашёл этот код в личных сообщениях боту. Отправьте боту код {code} и повторите подтверждение.");
            }

            return new TelegramChatBindingResult(
                confirmedMessage.Message.Chat.Id,
                true,
                $"Telegram chat linked at {now:O}.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "Telegram chat confirmation request failed. ExceptionType={ExceptionType}",
                ex.GetType().Name);
            return new TelegramChatBindingResult(
                null,
                false,
                "Не удалось связаться с Telegram. Повторите попытку.",
                "unreachable",
                "Telegram unreachable.");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(
                "Telegram chat confirmation request timed out. ExceptionType={ExceptionType}",
                ex.GetType().Name);
            return new TelegramChatBindingResult(
                null,
                false,
                "Telegram не ответил вовремя. Повторите попытку.",
                "unreachable",
                "Telegram timed out.");
        }
    }

    private static TelegramChatBindingResult CreateTelegramHttpFailureResult(HttpStatusCode statusCode, string? reasonPhrase)
    {
        var message = $"Unable to fetch Telegram updates: {(int)statusCode} {reasonPhrase}.";
        var (state, summary) = statusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ("auth_failed", $"Telegram auth failed ({(int)statusCode})."),
            HttpStatusCode.TooManyRequests => ("rate_limited", "Telegram rate limited the request."),
            _ => ("protocol_error", $"Telegram returned {(int)statusCode} {reasonPhrase}."),
        };

        return new TelegramChatBindingResult(null, false, message, state, summary);
    }

    private static TelegramChatBindingResult CreateTelegramPayloadFailureResult(string? description)
    {
        var message = string.IsNullOrWhiteSpace(description)
            ? "Telegram rejected the request."
            : $"Telegram rejected the request: {description}.";
        var deliveryState = string.IsNullOrWhiteSpace(description)
            ? "protocol_error"
            : description.Contains("token", StringComparison.OrdinalIgnoreCase)
                || description.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                || description.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
                    ? "auth_failed"
                    : "protocol_error";

        return new TelegramChatBindingResult(null, false, message, deliveryState, message);
    }

    private PendingTelegramChatLink? GetPendingTelegramChatLink(DateTimeOffset now)
    {
        lock (_telegramLinkSync)
        {
            if (_pendingTelegramChatLink is null)
            {
                return null;
            }

            if (_pendingTelegramChatLink.ExpiresAtUtc <= now)
            {
                _pendingTelegramChatLink = null;
                return null;
            }

            return _pendingTelegramChatLink;
        }
    }

    private TelegramChatLinkSessionDto? BuildPendingTelegramChatLinkDto(DateTimeOffset now, AgentConfigurationSnapshot snapshot)
    {
        var pendingLink = GetPendingTelegramChatLink(now);
        if (pendingLink is null)
        {
            return null;
        }

        return new TelegramChatLinkSessionDto(
            CreateVersionDto(),
            "pending",
            "Отправьте этот код боту в личном чате, затем нажмите «Подтвердить привязку».",
            pendingLink.Code,
            pendingLink.ExpiresAtUtc,
            BuildDeliveryHealth(snapshot));
    }

    private void ClearPendingTelegramChatLink()
    {
        lock (_telegramLinkSync)
        {
            _pendingTelegramChatLink = null;
        }
    }

    private static ComponentHealthDto BuildTelegramBindingDeliveryHealth(
        AgentConfigurationSnapshot snapshot,
        TelegramChatBindingResult bindingAttempt) =>
        bindingAttempt.DeliveryState is null
            ? BuildDeliveryHealth(snapshot)
            : new ComponentHealthDto(
                bindingAttempt.DeliveryState,
                bindingAttempt.DeliverySummary ?? bindingAttempt.Message);

    public ActionResponseDto Reload(int activeLoopbackPort)
    {
        var now = _timeProvider.GetUtcNow();
        _configuration.Reload();
        _configuration.RefreshBootstrapSnapshot(activeLoopbackPort);
        _controlState.InvalidateCachedHealth();
        _controlState.RecordIncident("configuration_reloaded", "info", "Agent configuration was reloaded from disk.", now);
        PublishConfigurationDiagnostics("reload", now);

        return new ActionResponseDto(
            CreateVersionDto(),
            new AgentActionResultDto("reload", "completed", "Configuration reloaded from disk.", now));
    }

    public RestartActionResponse Restart()
    {
        var now = _timeProvider.GetUtcNow();
        _controlState.RecordIncident("restart_requested", "warning", "Agent restart was requested via the loopback API.", now);
        var decision = _restartCoordinator.ScheduleRestart();

        return new RestartActionResponse(
            decision.ShouldStop,
            new ActionResponseDto(
                CreateVersionDto(),
                new AgentActionResultDto("restart-agent", decision.Outcome, decision.Message, now)));
    }

    private void PublishConfigurationDiagnostics(string source, DateTimeOffset occurredAtUtc)
    {
        foreach (var diagnostic in _configuration.GetDiagnostics())
        {
            var logLevel = string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase)
                ? LogLevel.Error
                : LogLevel.Warning;

            _logger.Log(
                logLevel,
                "Agent {Source} diagnostic {Kind}: {Summary}",
                source,
                diagnostic.Kind,
                diagnostic.Summary);
            _controlState.RecordIncident(diagnostic.Kind, diagnostic.Severity, diagnostic.Summary, occurredAtUtc);
        }
    }

    private void RecordDeliveryIncident(DeliveryHealthSnapshot health)
    {
        if (health.State is DeliveryHealthState.Healthy or DeliveryHealthState.Disabled)
        {
            return;
        }

        var severity = health.State is DeliveryHealthState.AuthFailed or DeliveryHealthState.ProtocolError
            ? "error"
            : "warning";
        _controlState.RecordIncident("delivery_degraded", severity, health.Summary, health.CheckedAtUtc ?? _timeProvider.GetUtcNow());
    }

    private static ApiVersionDto CreateVersionDto() =>
        new(LocalControlApiContract.CurrentVersion, LocalControlApiContract.MinimumSupportedVersion);

    private static LocalControlSettingsDto MapSettings(AgentSettings settings) =>
        new(
            settings.Loopback.Port,
            settings.Monitor.PollIntervalMs,
            settings.Monitor.IdleTimeoutSec,
            settings.Monitor.MinConfirmLifetimeSec,
            settings.Delivery.Mode,
            settings.Cloud.BackendBaseUrl,
            settings.Telegram.DeliveryEnabled,
            settings.Telegram.ChatId,
            settings.Monitor.IgnoredWindowTitlesText,
            settings.Notifications.DisconnectNotificationEnabled,
            settings.Notifications.GhostDisconnectMessageTemplate,
            settings.Notifications.ClientDisconnectedMessageTemplate,
            settings.Notifications.ProcessExitedNotificationEnabled,
            settings.Notifications.ProcessExitedMessageTemplate,
            settings.Notifications.DeadStartedNotificationEnabled,
            settings.Notifications.DeadStartedMessageTemplate);

    private static SecretPresenceDto BuildSecretPresence(AgentSecrets secrets) =>
        new(
            !string.IsNullOrWhiteSpace(secrets.LocalApiToken),
            !string.IsNullOrWhiteSpace(secrets.TelegramBotToken),
            !string.IsNullOrWhiteSpace(secrets.CloudAuthKey));

    private static ComponentHealthDto BuildDeliveryHealth(AgentConfigurationSnapshot snapshot)
    {
        if (string.Equals(snapshot.Settings.Delivery.Mode, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return new ComponentHealthDto("disabled", "Delivery mode is disabled.");
        }

        if (string.Equals(snapshot.Settings.Delivery.Mode, "Local", StringComparison.OrdinalIgnoreCase))
        {
            if (!snapshot.Settings.Telegram.DeliveryEnabled || snapshot.Settings.Telegram.ChatId is null)
            {
                return new ComponentHealthDto("misconfigured", "Telegram delivery requires DeliveryEnabled and a target chat id.");
            }

            if (string.IsNullOrWhiteSpace(snapshot.Secrets.TelegramBotToken))
            {
                return new ComponentHealthDto("not_configured", "Telegram delivery requires a stored bot token.");
            }

            return new ComponentHealthDto("configured", "Telegram local delivery is configured.");
        }

        if (!TryResolveCloudBackendUri(snapshot.Settings.Cloud.BackendBaseUrl, out _, out var backendUrlError))
        {
            return new ComponentHealthDto("misconfigured", backendUrlError);
        }

        if (string.IsNullOrWhiteSpace(snapshot.Secrets.CloudAuthKey))
        {
            return new ComponentHealthDto("not_configured", "Cloud delivery requires a stored auth key.");
        }

        return new ComponentHealthDto("configured", "Cloud delivery is configured.");
    }

    private static string MapNotificationOutcome(DeliveryHealthState state, bool delivered) =>
        delivered
            ? "completed"
            : state switch
            {
                DeliveryHealthState.Disabled or DeliveryHealthState.NotConfigured or DeliveryHealthState.Misconfigured => "rejected",
                _ => "failed",
            };

    private static ComponentHealthDto BuildBackendHealth(AgentConfigurationSnapshot snapshot)
    {
        if (!string.Equals(snapshot.Settings.Delivery.Mode, "Cloud", StringComparison.OrdinalIgnoreCase))
        {
            return new ComponentHealthDto("disabled", "Backend health applies only in Cloud mode.");
        }

        if (!TryResolveCloudBackendUri(snapshot.Settings.Cloud.BackendBaseUrl, out _, out var backendUrlError))
        {
            return new ComponentHealthDto("misconfigured", backendUrlError);
        }

        if (string.IsNullOrWhiteSpace(snapshot.Secrets.CloudAuthKey))
        {
            return new ComponentHealthDto("not_configured", "Cloud delivery requires a stored auth key.");
        }

        return new ComponentHealthDto("unknown", "Backend connectivity has not been checked yet.");
    }

    private static bool TryResolveCloudBackendUri(string? backendBaseUrl, out Uri backendUri, out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(backendBaseUrl))
        {
            backendUri = null!;
            errorMessage = "Cloud delivery requires an absolute BackendBaseUrl.";
            return false;
        }

        if (!Uri.TryCreate(backendBaseUrl, UriKind.Absolute, out var candidateBackendUri)
            || (candidateBackendUri.Scheme != Uri.UriSchemeHttp && candidateBackendUri.Scheme != Uri.UriSchemeHttps))
        {
            backendUri = null!;
            errorMessage = "BackendBaseUrl must be an absolute http(s) URL.";
            return false;
        }

        backendUri = candidateBackendUri;
        errorMessage = string.Empty;
        return true;
    }

    private static AgentIncidentDto MapIncident(AgentIncidentRecord incident) =>
        new(incident.Id, incident.Kind, incident.Severity, incident.Summary, incident.OccurredAtUtc);

    private static AgentConnectionDto MapConnection(AgentConnectionRecord connection) =>
        new(
            connection.Id,
            connection.ProcessName,
            connection.WindowTitle,
            connection.State,
            connection.ObservedAtUtc,
            connection.Forensics is null
                ? null
                : new AgentConnectionForensicsDto(
                    connection.Forensics.Kind,
                    connection.Forensics.Summary,
                    connection.Forensics.EstablishedRowCount,
                    connection.Forensics.ObservedRemotePorts));

    private static AgentActionResultDto MapActionResult(AgentActionResultRecord result) =>
        new(result.Action, result.Outcome, result.Message, result.OccurredAtUtc);

    private static ComponentHealthDto MapHealth(AgentComponentHealthRecord health) =>
        new(health.State, health.Summary, health.CheckedAtUtc, health.LastSuccessAtUtc);

    private static ComponentHealthDto MapDeliveryHealth(DeliveryHealthSnapshot health) =>
        new(MapDeliveryState(health.State), health.Summary, health.CheckedAtUtc, health.LastSuccessAtUtc);

    private static string MapDeliveryState(DeliveryHealthState state) =>
        state switch
        {
            DeliveryHealthState.Disabled => "disabled",
            DeliveryHealthState.Healthy => "healthy",
            DeliveryHealthState.NotConfigured => "not_configured",
            DeliveryHealthState.Misconfigured => "misconfigured",
            DeliveryHealthState.AuthFailed => "auth_failed",
            DeliveryHealthState.Unreachable => "unreachable",
            DeliveryHealthState.ProtocolError => "protocol_error",
            DeliveryHealthState.RateLimited => "rate_limited",
            _ => "unknown",
        };
}

internal sealed record RestartActionResponse(bool ShouldStop, ActionResponseDto Response);
internal sealed record PendingTelegramChatLink(string Code, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc);
internal sealed record TelegramChatBindingResult(long? ChatId, bool Success, string Message, string? DeliveryState = null, string? DeliverySummary = null);
internal sealed record TelegramGetUpdatesResponse(bool Ok, IReadOnlyList<TelegramUpdateDto>? Result, string? Description = null);
internal sealed record TelegramUpdateDto(long UpdateId, TelegramMessageDto? Message, TelegramMessageDto? EditedMessage);
internal sealed record TelegramUpdateMessageDto(long UpdateId, TelegramMessageDto? Message);
internal sealed record TelegramMessageDto(long Date, TelegramChatDto? Chat, string? Text = null);
internal sealed record TelegramChatDto(long Id, string? Type);


