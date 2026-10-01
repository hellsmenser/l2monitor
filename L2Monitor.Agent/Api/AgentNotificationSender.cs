using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using L2Monitor.Agent.Hosting;
using L2Monitor.Core.Delivery;
using L2Monitor.Core.Models;
using Microsoft.Extensions.Logging;

namespace L2Monitor.Agent;

internal interface IAgentNotificationSender
{
    Task<NotificationDispatchResult> SendTestNotificationAsync(
        AgentConfigurationSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task<NotificationDispatchResult> SendMonitorEventAsync(
        AgentConfigurationSnapshot snapshot,
        MonitorEvent monitorEvent,
        IReadOnlyDictionary<string, string?>? metadata = null,
        CancellationToken cancellationToken = default);
}

internal sealed class AgentNotificationSender(
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<AgentNotificationSender> logger) : IAgentNotificationSender
{
    internal const string TelegramHttpClientName = "telegram-secret-redacted";

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<AgentNotificationSender> _logger = logger;

    public Task<NotificationDispatchResult> SendTestNotificationAsync(
        AgentConfigurationSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var machineName = Environment.MachineName;
        var message = new NotificationMessage(
            new MonitorEvent(now.UtcDateTime, 0, machineName, MonitorEventKind.TestNotification, 0),
            $"Aden+: тестовое уведомление с компьютера {machineName} ({now:O})",
            now,
            new Dictionary<string, string?>
            {
                ["source"] = "local-control-api",
                ["machineName"] = machineName,
            });

        return ResolveMode(snapshot.Settings.Delivery.Mode) switch
        {
            DeliveryMode.Local => SendTelegramAsync(snapshot, message, cancellationToken),
            DeliveryMode.Cloud => RejectCloudNotification(now),
            _ => Task.FromResult(new NotificationDispatchResult(
                false,
                new DeliveryHealthSnapshot(
                    DeliveryMode.Disabled,
                    DeliveryHealthState.Disabled,
                    "Delivery mode is disabled.",
                    now)))
        };
    }

    public Task<NotificationDispatchResult> SendMonitorEventAsync(
        AgentConfigurationSnapshot snapshot,
        MonitorEvent monitorEvent,
        IReadOnlyDictionary<string, string?>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var occurredAt = new DateTimeOffset(monitorEvent.Time, TimeSpan.Zero);
        if (!IsNotificationEnabled(snapshot, monitorEvent.Kind))
        {
            return Task.FromResult(new NotificationDispatchResult(
                false,
                new DeliveryHealthSnapshot(
                    ResolveMode(snapshot.Settings.Delivery.Mode),
                    DeliveryHealthState.Disabled,
                    $"Notification type '{monitorEvent.Kind}' is disabled by settings.",
                    occurredAt),
                Suppressed: true));
        }

        var windowTitle = metadata is not null && metadata.TryGetValue("windowTitle", out var titleValue)
            ? titleValue
            : null;
        var message = new NotificationMessage(
            monitorEvent,
            BuildMonitorEventText(snapshot, monitorEvent, windowTitle),
            occurredAt,
            metadata ?? new Dictionary<string, string?>());

        return ResolveMode(snapshot.Settings.Delivery.Mode) switch
        {
            DeliveryMode.Local => SendTelegramAsync(snapshot, message, cancellationToken),
            DeliveryMode.Cloud => RejectCloudNotification(occurredAt),
            _ => Task.FromResult(new NotificationDispatchResult(
                false,
                new DeliveryHealthSnapshot(
                    DeliveryMode.Disabled,
                    DeliveryHealthState.Disabled,
                    "Delivery mode is disabled.",
                    occurredAt)))
        };
    }

    private async Task<NotificationDispatchResult> SendTelegramAsync(
        AgentConfigurationSnapshot snapshot,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (!snapshot.Settings.Telegram.DeliveryEnabled || snapshot.Settings.Telegram.ChatId is null)
        {
            return Reject(DeliveryMode.Local, DeliveryHealthState.Misconfigured, "Telegram delivery requires DeliveryEnabled and a target chat id.", now);
        }

        if (string.IsNullOrWhiteSpace(snapshot.Secrets.TelegramBotToken))
        {
            return Reject(DeliveryMode.Local, DeliveryHealthState.NotConfigured, "Telegram delivery requires a stored bot token.", now);
        }

        var client = _httpClientFactory.CreateClient(TelegramHttpClientName);
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["chat_id"] = snapshot.Settings.Telegram.ChatId.Value.ToString(),
                ["text"] = message.Text,
            });
            using var response = await client.PostAsync(
                $"https://api.telegram.org/bot{snapshot.Secrets.TelegramBotToken}/sendMessage",
                content,
                cancellationToken).ConfigureAwait(false);

            var health = await MapTelegramResponseAsync(
                response,
                snapshot.Settings.Telegram.ChatId.Value,
                now,
                cancellationToken).ConfigureAwait(false);
            LogDispatchOutcome(health, response.StatusCode);
            return new NotificationDispatchResult(health.State == DeliveryHealthState.Healthy, health);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                "Telegram delivery request failed. ExceptionType={ExceptionType}",
                ex.GetType().Name);
            return Reject(DeliveryMode.Local, DeliveryHealthState.Unreachable, "Telegram unreachable.", now);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Telegram delivery request timed out. ExceptionType={ExceptionType}",
                ex.GetType().Name);
            return Reject(DeliveryMode.Local, DeliveryHealthState.Unreachable, "Telegram timed out.", now);
        }
    }

    private Task<NotificationDispatchResult> RejectCloudNotification(DateTimeOffset now) =>
        Task.FromResult(Reject(
            DeliveryMode.Cloud,
            DeliveryHealthState.Disabled,
            "Для облачного режима проверка отправки недоступна.",
            now));

    private static NotificationDispatchResult CreateRejectedResult(
        DeliveryMode mode,
        DeliveryHealthState state,
        string summary,
        DateTimeOffset checkedAtUtc)
    {
        return new NotificationDispatchResult(
            false,
            new DeliveryHealthSnapshot(mode, state, summary, checkedAtUtc));
    }

    private static DeliveryHealthSnapshot MapResponse(
        DeliveryMode mode,
        HttpResponseMessage response,
        string successSummary,
        DateTimeOffset checkedAtUtc)
    {
        if (response.IsSuccessStatusCode)
        {
            return new DeliveryHealthSnapshot(mode, DeliveryHealthState.Healthy, successSummary, checkedAtUtc, checkedAtUtc);
        }

        var (state, summary) = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                (DeliveryHealthState.AuthFailed, $"{DescribeMode(mode)} auth failed ({(int)response.StatusCode})."),
            HttpStatusCode.TooManyRequests =>
                (DeliveryHealthState.RateLimited, $"{DescribeMode(mode)} rate limited the request."),
            _ =>
                (DeliveryHealthState.ProtocolError, $"{DescribeMode(mode)} returned {(int)response.StatusCode} {response.ReasonPhrase}."),
        };

        return new DeliveryHealthSnapshot(mode, state, summary, checkedAtUtc);
    }

    private static async Task<DeliveryHealthSnapshot> MapTelegramResponseAsync(
        HttpResponseMessage response,
        long chatId,
        DateTimeOffset checkedAtUtc,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return MapResponse(
                DeliveryMode.Local,
                response,
                $"Telegram delivered to chat_id={chatId}.",
                checkedAtUtc);
        }

        var payload = await response.Content.ReadFromJsonAsync<TelegramApiResponse>(cancellationToken).ConfigureAwait(false);
        if (payload is null || payload.Ok)
        {
            return new DeliveryHealthSnapshot(
                DeliveryMode.Local,
                DeliveryHealthState.Healthy,
                $"Telegram delivered to chat_id={chatId}.",
                checkedAtUtc,
                checkedAtUtc);
        }

        var summary = string.IsNullOrWhiteSpace(payload.Description)
            ? "Telegram rejected the request."
            : $"Telegram rejected the request: {payload.Description}.";
        var state = string.IsNullOrWhiteSpace(payload.Description)
            ? DeliveryHealthState.ProtocolError
            : payload.Description.Contains("token", StringComparison.OrdinalIgnoreCase)
                || payload.Description.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                || payload.Description.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
                    ? DeliveryHealthState.AuthFailed
                    : DeliveryHealthState.ProtocolError;

        return new DeliveryHealthSnapshot(DeliveryMode.Local, state, summary, checkedAtUtc);
    }

    private static DeliveryMode ResolveMode(string? mode) =>
        string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase)
            ? DeliveryMode.Local
            : string.Equals(mode, "Cloud", StringComparison.OrdinalIgnoreCase)
                ? DeliveryMode.Cloud
                : DeliveryMode.Disabled;

    private static string DescribeMode(DeliveryMode mode) =>
        mode == DeliveryMode.Local ? "Telegram" : "Cloud relay";

    private static bool IsNotificationEnabled(AgentConfigurationSnapshot snapshot, MonitorEventKind kind) =>
        kind switch
        {
            MonitorEventKind.GhostDisconnectSuspected or MonitorEventKind.ClientDisconnected =>
                snapshot.Settings.Notifications.DisconnectNotificationEnabled,
            MonitorEventKind.ProcessExited => snapshot.Settings.Notifications.ProcessExitedNotificationEnabled,
            MonitorEventKind.DeadStarted => snapshot.Settings.Notifications.DeadStartedNotificationEnabled,
            _ => true,
        };

    private static string BuildMonitorEventText(
        AgentConfigurationSnapshot snapshot,
        MonitorEvent monitorEvent,
        string? windowTitle)
    {
        var customTemplate = monitorEvent.Kind switch
        {
            MonitorEventKind.GhostDisconnectSuspected or MonitorEventKind.ClientDisconnected =>
                snapshot.Settings.Notifications.GhostDisconnectMessageTemplate
                ?? snapshot.Settings.Notifications.ClientDisconnectedMessageTemplate,
            MonitorEventKind.ProcessExited => snapshot.Settings.Notifications.ProcessExitedMessageTemplate,
            MonitorEventKind.DeadStarted => snapshot.Settings.Notifications.DeadStartedMessageTemplate,
            _ => null,
        };

        return BuildMonitorEventText(snapshot, monitorEvent, windowTitle, pickVariantIndex: null);
    }

    internal static string BuildMonitorEventText(
        AgentConfigurationSnapshot snapshot,
        MonitorEvent monitorEvent,
        string? windowTitle,
        Func<int, int>? pickVariantIndex)
    {
        var customTemplate = monitorEvent.Kind switch
        {
            MonitorEventKind.GhostDisconnectSuspected or MonitorEventKind.ClientDisconnected =>
                snapshot.Settings.Notifications.GhostDisconnectMessageTemplate
                ?? snapshot.Settings.Notifications.ClientDisconnectedMessageTemplate,
            MonitorEventKind.ProcessExited => snapshot.Settings.Notifications.ProcessExitedMessageTemplate,
            MonitorEventKind.DeadStarted => snapshot.Settings.Notifications.DeadStartedMessageTemplate,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(customTemplate))
        {
            return BuildDefaultMonitorEventText(monitorEvent);
        }

        var selectedTemplate = SelectTemplateVariant(customTemplate, pickVariantIndex);
        return string.IsNullOrWhiteSpace(selectedTemplate)
            ? BuildDefaultMonitorEventText(monitorEvent)
            : RenderTemplate(selectedTemplate, monitorEvent, windowTitle);
    }

    private static string BuildDefaultMonitorEventText(MonitorEvent monitorEvent) =>
        monitorEvent.Kind switch
        {
            MonitorEventKind.GhostDisconnectSuspected =>
                $"Aden+: возможный дисконнект {monitorEvent.Name} (PID {monitorEvent.Pid}): игровой сокет активен, но сокет сессии пропал.",
            MonitorEventKind.ClientDisconnected =>
                $"Aden+: {monitorEvent.Name} (PID {monitorEvent.Pid}) потерял активное игровое соединение.",
            MonitorEventKind.ProcessExited =>
                $"Aden+: клиент {monitorEvent.Name} (PID {monitorEvent.Pid}) закрыт.",
            MonitorEventKind.ProcessExitedWhileDead =>
                $"Aden+: клиент {monitorEvent.Name} (PID {monitorEvent.Pid}) закрыт после дисконнекта через {monitorEvent.DurationSec} с.",
            MonitorEventKind.TimeoutExceeded =>
                $"Aden+: клиент {monitorEvent.Name} (PID {monitorEvent.Pid}) не отвечает {monitorEvent.DurationSec} с.",
            MonitorEventKind.IdleBack =>
                $"Aden+: клиент {monitorEvent.Name} (PID {monitorEvent.Pid}) восстановил соединение через {monitorEvent.DurationSec} с.",
            MonitorEventKind.DeadStarted =>
                $"Aden+: обнаружен звуковой признак смерти персонажа в {monitorEvent.Name} (PID {monitorEvent.Pid}).",
            _ =>
                $"Aden+: событие {monitorEvent.Kind} для {monitorEvent.Name} (PID {monitorEvent.Pid})."
        };

    private static string RenderTemplate(string template, MonitorEvent monitorEvent, string? windowTitle) =>
        template
            .Replace("{pid}", monitorEvent.Pid.ToString(), StringComparison.Ordinal)
            .Replace("{name}", monitorEvent.Name, StringComparison.Ordinal)
            .Replace("{event}", monitorEvent.Kind.ToString(), StringComparison.Ordinal)
            .Replace("{durationSec}", monitorEvent.DurationSec.ToString(), StringComparison.Ordinal)
            .Replace("{windowTitle}", windowTitle ?? string.Empty, StringComparison.Ordinal);

    private static string? SelectTemplateVariant(string template, Func<int, int>? pickVariantIndex)
    {
        var variants = template
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (variants.Length == 0)
        {
            return null;
        }

        if (variants.Length == 1)
        {
            return variants[0];
        }

        var index = pickVariantIndex is null
            ? RandomNumberGenerator.GetInt32(variants.Length)
            : pickVariantIndex(variants.Length);

        if (index < 0 || index >= variants.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(pickVariantIndex), index, "Variant index must be within template bounds.");
        }

        return variants[index];
    }

    private NotificationDispatchResult Reject(
        DeliveryMode mode,
        DeliveryHealthState state,
        string summary,
        DateTimeOffset checkedAtUtc)
    {
        var result = CreateRejectedResult(mode, state, summary, checkedAtUtc);
        LogDispatchOutcome(result.Health, null);
        return result;
    }

    private void LogDispatchOutcome(DeliveryHealthSnapshot health, HttpStatusCode? statusCode)
    {
        var mode = DescribeMode(health.Mode);
        if (health.State == DeliveryHealthState.Healthy)
        {
            _logger.LogInformation(
                "{Mode} delivery succeeded. Summary={Summary} StatusCode={StatusCode}",
                mode,
                health.Summary,
                statusCode is null ? "n/a" : ((int)statusCode).ToString());
            return;
        }

        _logger.LogWarning(
            "{Mode} delivery degraded. State={State} Summary={Summary} StatusCode={StatusCode}",
            mode,
            health.State,
            health.Summary,
            statusCode is null ? "n/a" : ((int)statusCode).ToString());
    }

    private sealed record TelegramApiResponse(bool Ok, string? Description = null);
}
