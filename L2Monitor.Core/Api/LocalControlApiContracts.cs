using System.Text.Json.Serialization;

namespace L2Monitor.Core.Api;

public static class LocalControlApiContract
{
    public const string CurrentVersion = "1.0";
    public const string MinimumSupportedVersion = "1.0";
    public const string VersionHeaderName = "X-L2Monitor-Api-Version";
    public const string MinimumVersionHeaderName = "X-L2Monitor-Min-Api-Version";
    public const string AuthHeaderName = "Authorization";
}

public sealed record ApiVersionDto(
    string Current,
    string MinimumSupported);

public sealed record ComponentHealthDto(
    string State,
    string Summary,
    DateTimeOffset? CheckedAtUtc = null,
    DateTimeOffset? LastSuccessAtUtc = null);

public sealed record ClientUpdateDto(
    string State,
    string CurrentVersion,
    string? LatestVersion,
    bool IsUpdateAvailable,
    bool Required,
    string? ReleaseUrl,
    DateTimeOffset? CheckedAtUtc);

public sealed record AgentActionResultDto(
    string Action,
    string Outcome,
    string Message,
    DateTimeOffset OccurredAtUtc);

public sealed record AgentIncidentDto(
    string Id,
    string Kind,
    string Severity,
    string Summary,
    DateTimeOffset OccurredAtUtc);

public sealed record AgentConnectionDto(
    string Id,
    string ProcessName,
    string? WindowTitle,
    string State,
    DateTimeOffset ObservedAtUtc,
    AgentConnectionForensicsDto? Forensics = null);

public sealed record AgentConnectionForensicsDto(
    string Kind,
    string Summary,
    int EstablishedRowCount,
    IReadOnlyList<int> ObservedRemotePorts);

[method: JsonConstructor]
public sealed record LocalControlSettingsDto(
    int LoopbackPort,
    int PollIntervalMs,
    int IdleTimeoutSec,
    int MinConfirmLifetimeSec,
    string DeliveryMode,
    string? BackendBaseUrl,
    bool TelegramDeliveryEnabled,
    long? TelegramChatId,
    string? IgnoredWindowTitlesText,
    bool DisconnectNotificationEnabled,
    string? GhostDisconnectMessageTemplate,
    string? ClientDisconnectedMessageTemplate,
    bool ProcessExitedNotificationEnabled,
    string? ProcessExitedMessageTemplate,
    bool DeadStartedNotificationEnabled,
    string? DeadStartedMessageTemplate = null)
{
    public LocalControlSettingsDto(
        int LoopbackPort,
        int PollIntervalMs,
        int IdleTimeoutSec,
        int MinConfirmLifetimeSec,
        string DeliveryMode,
        string? BackendBaseUrl,
        bool TelegramDeliveryEnabled,
        long? TelegramChatId,
        string? IgnoredWindowTitlesText,
        string? GhostDisconnectMessageTemplate,
        string? ClientDisconnectedMessageTemplate,
        string? ProcessExitedMessageTemplate,
        string? DeadStartedMessageTemplate = null)
        : this(
            LoopbackPort,
            PollIntervalMs,
            IdleTimeoutSec,
            MinConfirmLifetimeSec,
            DeliveryMode,
            BackendBaseUrl,
            TelegramDeliveryEnabled,
            TelegramChatId,
            IgnoredWindowTitlesText,
            true,
            GhostDisconnectMessageTemplate,
            ClientDisconnectedMessageTemplate,
            true,
            ProcessExitedMessageTemplate,
            true,
            DeadStartedMessageTemplate)
    {
    }

    public LocalControlSettingsDto(
        int LoopbackPort,
        int PollIntervalMs,
        int IdleTimeoutSec,
        int MinConfirmLifetimeSec,
        string DeliveryMode,
        string? BackendBaseUrl,
        bool TelegramDeliveryEnabled,
        long? TelegramChatId)
        : this(
            LoopbackPort,
            PollIntervalMs,
            IdleTimeoutSec,
            MinConfirmLifetimeSec,
            DeliveryMode,
            BackendBaseUrl,
            TelegramDeliveryEnabled,
            TelegramChatId,
            null,
            null,
            null,
            null,
            null)
    {
    }
}

public sealed record SecretPresenceDto(
    bool HasLocalApiToken,
    bool HasTelegramBotToken,
    bool HasCloudAuthKey);

public sealed record StatusResponseDto(
    ApiVersionDto ApiVersion,
    string AgentStatus,
    string CurrentMode,
    int ActiveLoopbackPort,
    int ActiveConnectionCount,
    int ProbeCount,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? LastScanAtUtc,
    ComponentHealthDto Delivery,
    ComponentHealthDto Backend,
    AgentIncidentDto? LastIncident,
    AgentActionResultDto? LastNotificationResult,
    IReadOnlyList<AgentConnectionDto> Connections,
    ClientUpdateDto? Update = null);

public sealed record ConnectionsResponseDto(
    ApiVersionDto ApiVersion,
    IReadOnlyList<AgentConnectionDto> Items);

public sealed record IncidentsResponseDto(
    ApiVersionDto ApiVersion,
    IReadOnlyList<AgentIncidentDto> Items);

public sealed record SettingsResponseDto(
    ApiVersionDto ApiVersion,
    int ActiveLoopbackPort,
    LocalControlSettingsDto Settings,
    SecretPresenceDto Secrets,
    TelegramChatLinkSessionDto? PendingTelegramChatLink = null);

public sealed record UpdateSettingsRequestDto(
    LocalControlSettingsDto Settings);

[method: JsonConstructor]
public sealed record TrayEditableSettingsDto(
    string DeliveryMode,
    bool TelegramDeliveryEnabled,
    string? IgnoredWindowTitlesText,
    bool DisconnectNotificationEnabled,
    string? GhostDisconnectMessageTemplate,
    bool ProcessExitedNotificationEnabled,
    string? ProcessExitedMessageTemplate,
    bool DeadStartedNotificationEnabled,
    string? DeadStartedMessageTemplate = null,
    string? BackendBaseUrl = null)
{
    public TrayEditableSettingsDto(
        string DeliveryMode,
        bool TelegramDeliveryEnabled,
        string? IgnoredWindowTitlesText,
        string? GhostDisconnectMessageTemplate,
        string? ProcessExitedMessageTemplate,
        string? DeadStartedMessageTemplate = null)
        : this(
            DeliveryMode,
            TelegramDeliveryEnabled,
            IgnoredWindowTitlesText,
            true,
            GhostDisconnectMessageTemplate,
            true,
            ProcessExitedMessageTemplate,
            true,
            DeadStartedMessageTemplate,
            null)
    {
    }
}

public sealed record UpdateTraySettingsRequestDto(
    TrayEditableSettingsDto Settings);

public sealed record UpdateTelegramSecretRequestDto(
    string BotToken);

public sealed record UpdateCloudSecretRequestDto(
    string AuthKey);

public sealed record TelegramChatLinkSessionDto(
    ApiVersionDto ApiVersion,
    string Outcome,
    string Message,
    string? Code,
    DateTimeOffset? ExpiresAtUtc,
    ComponentHealthDto? Delivery = null);

public sealed record ActionResponseDto(
    ApiVersionDto ApiVersion,
    AgentActionResultDto Result,
    ComponentHealthDto? Delivery = null,
    ComponentHealthDto? Backend = null);

public sealed record ErrorResponseDto(
    ApiVersionDto ApiVersion,
    string Error,
    string Message);
