using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using L2Monitor.Core.Api;
using L2Monitor.Core.Storage;

namespace L2Monitor.Agent.Hosting;

internal sealed class AgentConfigurationService
{
    private const int LegacyDefaultMinConfirmLifetimeSec = 5;
    private const int CurrentDefaultMinConfirmLifetimeSec = 15;
    private const string SettingsFileName = "settings.json";
    private const string SecretsFileName = "secrets.dat";
    private const string PackagedDefaultsFileName = "adenplus.defaults.json";
    private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("L2Monitor.Agent.SecretStore.v1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    private readonly object _sync = new();
    private AgentConfigurationSnapshot _snapshot;
    private IReadOnlyList<AgentConfigurationDiagnostic> _diagnostics = [];
    private bool _skipSecretsPersistenceUntilExplicitRewrite;
    private AgentConfigurationDiagnostic? _persistentSecretsLoadFailureDiagnostic;

    public AgentConfigurationService()
    {
        var loadResult = LoadSnapshot();
        _snapshot = loadResult.Snapshot;
        _diagnostics = loadResult.Diagnostics;
        _skipSecretsPersistenceUntilExplicitRewrite = loadResult.SkipSecretsPersistenceUntilExplicitRewrite;
        _persistentSecretsLoadFailureDiagnostic = loadResult.PersistentSecretsLoadFailureDiagnostic;
        EnsureLocalApiToken();
        SaveBootstrapSnapshot();
    }

    public AgentConfigurationSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public IReadOnlyList<AgentConfigurationDiagnostic> GetDiagnostics()
    {
        lock (_sync)
        {
            return [.. _diagnostics];
        }
    }

    public bool IsRecoveryModeActive()
    {
        lock (_sync)
        {
            return _persistentSecretsLoadFailureDiagnostic is not null;
        }
    }

    public AgentConfigurationSnapshot UpdateSettings(LocalControlSettingsDto dto)
    {
        lock (_sync)
        {
            var deliveryMode = NormalizeDeliveryMode(dto.DeliveryMode);
            var diagnostics = CreateCurrentDiagnosticsList();
            var settings = NormalizeSettings(
                new AgentSettings
                {
                    Loopback = new LoopbackSettings
                    {
                        Port = dto.LoopbackPort,
                    },
                    Monitor = new AgentMonitorSettings
                    {
                        PollIntervalMs = dto.PollIntervalMs,
                        IdleTimeoutSec = dto.IdleTimeoutSec,
                        MinConfirmLifetimeSec = dto.MinConfirmLifetimeSec,
                        IgnoredWindowTitlesText = NormalizeMultilineText(dto.IgnoredWindowTitlesText) ?? string.Empty,
                    },
                    Delivery = new DeliverySettings
                    {
                        Mode = deliveryMode,
                    },
                    Telegram = new TelegramSettings
                    {
                        DeliveryEnabled = dto.TelegramDeliveryEnabled,
                        ChatId = dto.TelegramChatId,
                    },
                    Cloud = new CloudSettings
                    {
                        BackendBaseUrl = dto.BackendBaseUrl,
                    },
                    Notifications = new AgentNotificationSettings
                    {
                        DisconnectNotificationEnabled = dto.DisconnectNotificationEnabled,
                        GhostDisconnectMessageTemplate = NormalizeOptionalString(dto.GhostDisconnectMessageTemplate),
                        ClientDisconnectedMessageTemplate = NormalizeOptionalString(dto.ClientDisconnectedMessageTemplate),
                        ProcessExitedNotificationEnabled = dto.ProcessExitedNotificationEnabled,
                        ProcessExitedMessageTemplate = NormalizeOptionalString(dto.ProcessExitedMessageTemplate),
                        DeadStartedNotificationEnabled = dto.DeadStartedNotificationEnabled,
                        DeadStartedMessageTemplate = NormalizeOptionalString(dto.DeadStartedMessageTemplate),
                    },
                },
                diagnostics);

            _snapshot = _snapshot with
            {
                Settings = settings,
            };

            SaveSettings(_snapshot.Settings);
            _diagnostics = FinalizeDiagnostics(diagnostics, _snapshot);
            return _snapshot;
        }
    }

    public AgentConfigurationSnapshot UpdateTraySettings(TrayEditableSettingsDto dto)
    {
        lock (_sync)
        {
            var diagnostics = CreateCurrentDiagnosticsList();
            var settings = NormalizeSettings(
                _snapshot.Settings with
                {
                    Delivery = _snapshot.Settings.Delivery with
                    {
                        Mode = NormalizeDeliveryMode(dto.DeliveryMode),
                    },
                    Cloud = _snapshot.Settings.Cloud with
                    {
                        BackendBaseUrl = NormalizeOptionalString(dto.BackendBaseUrl)
                            ?? _snapshot.Settings.Cloud.BackendBaseUrl,
                    },
                    Telegram = _snapshot.Settings.Telegram with
                    {
                        DeliveryEnabled = dto.TelegramDeliveryEnabled,
                    },
                    Monitor = _snapshot.Settings.Monitor with
                    {
                        IgnoredWindowTitlesText = NormalizeMultilineText(dto.IgnoredWindowTitlesText) ?? string.Empty,
                    },
                    Notifications = _snapshot.Settings.Notifications with
                    {
                        DisconnectNotificationEnabled = dto.DisconnectNotificationEnabled,
                        GhostDisconnectMessageTemplate = NormalizeOptionalString(dto.GhostDisconnectMessageTemplate),
                        ClientDisconnectedMessageTemplate = NormalizeOptionalString(dto.GhostDisconnectMessageTemplate),
                        ProcessExitedNotificationEnabled = dto.ProcessExitedNotificationEnabled,
                        ProcessExitedMessageTemplate = NormalizeOptionalString(dto.ProcessExitedMessageTemplate),
                        DeadStartedNotificationEnabled = dto.DeadStartedNotificationEnabled,
                        DeadStartedMessageTemplate = NormalizeOptionalString(dto.DeadStartedMessageTemplate),
                    },
                },
                diagnostics);

            _snapshot = _snapshot with
            {
                Settings = settings,
            };

            SaveSettings(_snapshot.Settings);
            _diagnostics = FinalizeDiagnostics(diagnostics, _snapshot);
            return _snapshot;
        }
    }

    public AgentConfigurationSnapshot UpdateTelegramSecret(string botToken)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                Secrets = _snapshot.Secrets with
                {
                    TelegramBotToken = NormalizeOptionalString(botToken) ?? string.Empty,
                },
            };

            SaveSecrets(_snapshot.Secrets);
            _skipSecretsPersistenceUntilExplicitRewrite = false;
            _persistentSecretsLoadFailureDiagnostic = null;
            return _snapshot;
        }
    }

    public AgentConfigurationSnapshot UpdateTelegramChatId(long chatId)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                Settings = _snapshot.Settings with
                {
                    Telegram = _snapshot.Settings.Telegram with
                    {
                        ChatId = chatId,
                    },
                },
            };

            SaveSettings(_snapshot.Settings);
            return _snapshot;
        }
    }

    public AgentConfigurationSnapshot UpdateCloudSecret(string authKey)
    {
        lock (_sync)
        {
            _snapshot = _snapshot with
            {
                Secrets = _snapshot.Secrets with
                {
                    CloudAuthKey = NormalizeOptionalString(authKey) ?? string.Empty,
                },
            };

            SaveSecrets(_snapshot.Secrets);
            _skipSecretsPersistenceUntilExplicitRewrite = false;
            _persistentSecretsLoadFailureDiagnostic = null;
            return _snapshot;
        }
    }

    public AgentConfigurationSnapshot Reload()
    {
        lock (_sync)
        {
            var loadResult = LoadSnapshot();
            _snapshot = loadResult.Snapshot;
            _diagnostics = loadResult.Diagnostics;
            _skipSecretsPersistenceUntilExplicitRewrite = loadResult.SkipSecretsPersistenceUntilExplicitRewrite;
            _persistentSecretsLoadFailureDiagnostic = loadResult.PersistentSecretsLoadFailureDiagnostic;
            EnsureLocalApiToken();
            return _snapshot;
        }
    }

    public static AgentSettings LoadBootstrapSettings()
    {
        var settingsPath = Path.Combine(GetRootDirectory(), SettingsFileName);
        if (!File.Exists(settingsPath))
        {
            return LoadPackagedDefaults();
        }

        try
        {
            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<AgentSettings>(json, JsonOptions);
            return NormalizeSettings(ApplyPersistedSettingsMigrations(settings));
        }
        catch
        {
            return LoadPackagedDefaults();
        }
    }

    internal static string ResolveRootDirectory(string? baseDirectory = null)
        => AgentStorageLayout.ResolveRootDirectory(baseDirectory);

    private static string GetRootDirectory() => ResolveRootDirectory();

    private LoadSnapshotResult LoadSnapshot()
    {
        Directory.CreateDirectory(GetRootDirectory());

        var diagnostics = new List<AgentConfigurationDiagnostic>();
        var settings = LoadSettings(diagnostics);
        var secretsResult = LoadSecrets();
        if (secretsResult.LoadFailureDiagnostic is not null)
        {
            diagnostics.Add(secretsResult.LoadFailureDiagnostic);
        }

        var secrets = secretsResult.Secrets;
        var snapshot = new AgentConfigurationSnapshot(settings, secrets);
        diagnostics.AddRange(BuildRuntimeDiagnostics(snapshot));
        return new LoadSnapshotResult(
            snapshot,
            diagnostics,
            secretsResult.SkipPersistenceUntilExplicitRewrite,
            secretsResult.LoadFailureDiagnostic);
    }

    private AgentSettings LoadSettings(List<AgentConfigurationDiagnostic> diagnostics)
    {
        var settingsPath = Path.Combine(GetRootDirectory(), SettingsFileName);
        if (!File.Exists(settingsPath))
        {
            return LoadPackagedDefaults();
        }

        try
        {
            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<AgentSettings>(json, JsonOptions);
            return NormalizeSettings(ApplyPersistedSettingsMigrations(settings), diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics.Add(new AgentConfigurationDiagnostic(
                "settings_load_failed",
                "error",
                $"Unable to load settings.json; using defaults. {ex.Message}"));
            return LoadPackagedDefaults();
        }
    }

    internal static AgentSettings LoadPackagedDefaults(string? baseDirectory = null)
    {
        var defaultsPath = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, PackagedDefaultsFileName);
        if (!File.Exists(defaultsPath))
        {
            return AgentSettings.Default;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(defaultsPath));
            if (!document.RootElement.TryGetProperty("backendBaseUrl", out var backendBaseUrlElement))
            {
                return AgentSettings.Default;
            }

            var backendBaseUrl = NormalizeOptionalString(backendBaseUrlElement.GetString());
            if (!Uri.TryCreate(backendBaseUrl, UriKind.Absolute, out var backendUri)
                || (backendUri.Scheme != Uri.UriSchemeHttp && backendUri.Scheme != Uri.UriSchemeHttps))
            {
                return AgentSettings.Default;
            }

            return AgentSettings.Default with
            {
                Cloud = AgentSettings.Default.Cloud with
                {
                    BackendBaseUrl = backendUri.AbsoluteUri,
                },
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return AgentSettings.Default;
        }
    }

    private LoadSecretsResult LoadSecrets()
    {
        var secretsPath = Path.Combine(GetRootDirectory(), SecretsFileName);
        if (!File.Exists(secretsPath))
        {
            return new LoadSecretsResult(AgentSecrets.Empty, false, null);
        }

        var payload = File.ReadAllBytes(secretsPath);
        if (payload.Length == 0)
        {
            return new LoadSecretsResult(AgentSecrets.Empty, false, null);
        }

        try
        {
            var plainBytes = ProtectedData.Unprotect(payload, SecretEntropy, DataProtectionScope.CurrentUser);
            return new LoadSecretsResult(
                JsonSerializer.Deserialize<AgentSecrets>(plainBytes, JsonOptions) ?? AgentSecrets.Empty,
                false,
                null);
        }
        catch (Exception ex)
        {
            return new LoadSecretsResult(
                AgentSecrets.Empty,
                true,
                new AgentConfigurationDiagnostic(
                    "secrets_load_failed",
                    "error",
                    $"Unable to load secrets.dat; stored secrets are unavailable until re-entered. {ex.Message}"));
        }
    }

    private void SaveSettings(AgentSettings settings)
    {
        var path = Path.Combine(GetRootDirectory(), SettingsFileName);
        WriteFileAtomically(path, stream =>
        {
            JsonSerializer.Serialize(stream, settings, JsonOptions);
        });
    }

    private void SaveSecrets(AgentSecrets secrets)
    {
        var path = Path.Combine(GetRootDirectory(), SecretsFileName);
        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(secrets, JsonOptions);
        var protectedBytes = ProtectedData.Protect(plainBytes, SecretEntropy, DataProtectionScope.CurrentUser);
        WriteFileAtomically(path, stream =>
        {
            stream.Write(protectedBytes);
        });
    }

    private static void WriteFileAtomically(string path, Action<FileStream> writePayload)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Configuration file path must have a parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                writePayload(stream);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private void EnsureLocalApiToken()
    {
        if (!string.IsNullOrWhiteSpace(_snapshot.Secrets.LocalApiToken))
        {
            return;
        }

        _snapshot = _snapshot with
        {
            Secrets = _snapshot.Secrets with
            {
                LocalApiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            },
        };

        if (!_skipSecretsPersistenceUntilExplicitRewrite)
        {
            SaveSecrets(_snapshot.Secrets);
        }
    }

    private void SaveBootstrapSnapshot()
        => SaveBootstrapSnapshot(_snapshot.Settings.Loopback.Port);

    internal void RefreshBootstrapSnapshot(int activeLoopbackPort)
    {
        lock (_sync)
        {
            SaveBootstrapSnapshot(activeLoopbackPort);
        }
    }

    private void SaveBootstrapSnapshot(int loopbackPort)
    {
        var token = _skipSecretsPersistenceUntilExplicitRewrite
            ? null
            : NormalizeOptionalString(_snapshot.Secrets.LocalApiToken);
        AgentBootstrapStore.Save(new AgentBootstrapRecord(loopbackPort, token));
    }

    private List<AgentConfigurationDiagnostic> CreateCurrentDiagnosticsList()
    {
        var diagnostics = new List<AgentConfigurationDiagnostic>();
        if (_persistentSecretsLoadFailureDiagnostic is not null)
        {
            diagnostics.Add(_persistentSecretsLoadFailureDiagnostic);
        }

        return diagnostics;
    }

    private static IReadOnlyList<AgentConfigurationDiagnostic> FinalizeDiagnostics(
        List<AgentConfigurationDiagnostic> diagnostics,
        AgentConfigurationSnapshot snapshot)
    {
        diagnostics.AddRange(BuildRuntimeDiagnostics(snapshot));
        return diagnostics;
    }

    private static int NormalizePort(int port) =>
        port is >= 1024 and <= 65535 ? port : AgentSettings.Default.Loopback.Port;

    private static int NormalizePollInterval(int pollIntervalMs) =>
        Math.Clamp(pollIntervalMs, 250, 300_000);

    private static AgentSettings ApplyPersistedSettingsMigrations(AgentSettings? settings)
    {
        settings ??= AgentSettings.Default;

        if (settings.Monitor.MinConfirmLifetimeSec == LegacyDefaultMinConfirmLifetimeSec)
        {
            settings = settings with
            {
                Monitor = settings.Monitor with
                {
                    MinConfirmLifetimeSec = CurrentDefaultMinConfirmLifetimeSec,
                },
            };
        }

        return settings;
    }

    private static AgentSettings NormalizeSettings(
        AgentSettings? settings,
        List<AgentConfigurationDiagnostic>? diagnostics = null)
    {
        settings ??= AgentSettings.Default;

        var normalizedLoopbackPort = NormalizePort(settings.Loopback.Port);
        if (normalizedLoopbackPort != settings.Loopback.Port)
        {
            diagnostics?.Add(new AgentConfigurationDiagnostic(
                "settings_loopback_port_invalid",
                "warning",
                $"Loopback.Port value '{settings.Loopback.Port}' is invalid; using {normalizedLoopbackPort}."));
        }

        var normalizedPollInterval = NormalizePollInterval(settings.Monitor.PollIntervalMs);
        if (normalizedPollInterval != settings.Monitor.PollIntervalMs)
        {
            diagnostics?.Add(new AgentConfigurationDiagnostic(
                "settings_poll_interval_invalid",
                "warning",
                $"Monitor.PollIntervalMs value '{settings.Monitor.PollIntervalMs}' is out of range; using {normalizedPollInterval}."));
        }

        var normalizedIdleTimeout = Math.Max(1, settings.Monitor.IdleTimeoutSec);
        if (normalizedIdleTimeout != settings.Monitor.IdleTimeoutSec)
        {
            diagnostics?.Add(new AgentConfigurationDiagnostic(
                "settings_idle_timeout_invalid",
                "warning",
                $"Monitor.IdleTimeoutSec value '{settings.Monitor.IdleTimeoutSec}' is out of range; using {normalizedIdleTimeout}."));
        }

        var normalizedMinConfirmLifetime = Math.Max(0, settings.Monitor.MinConfirmLifetimeSec);
        if (normalizedMinConfirmLifetime != settings.Monitor.MinConfirmLifetimeSec)
        {
            diagnostics?.Add(new AgentConfigurationDiagnostic(
                "settings_min_confirm_lifetime_invalid",
                "warning",
                $"Monitor.MinConfirmLifetimeSec value '{settings.Monitor.MinConfirmLifetimeSec}' is out of range; using {normalizedMinConfirmLifetime}."));
        }

        string normalizedDeliveryMode;
        try
        {
            normalizedDeliveryMode = NormalizeDeliveryMode(settings.Delivery.Mode);
        }
        catch (AgentSettingsValidationException)
        {
            normalizedDeliveryMode = AgentSettings.Default.Delivery.Mode;
            diagnostics?.Add(new AgentConfigurationDiagnostic(
                "settings_delivery_mode_invalid",
                "warning",
                $"Delivery.Mode value '{settings.Delivery.Mode}' is invalid; using {normalizedDeliveryMode}."));
        }

        var disconnectTemplate = NormalizeOptionalString(
            settings.Notifications.GhostDisconnectMessageTemplate
            ?? settings.Notifications.ClientDisconnectedMessageTemplate);

        return settings with
        {
            Loopback = settings.Loopback with
            {
                Port = normalizedLoopbackPort,
            },
            Monitor = settings.Monitor with
            {
                PollIntervalMs = normalizedPollInterval,
                IdleTimeoutSec = normalizedIdleTimeout,
                MinConfirmLifetimeSec = normalizedMinConfirmLifetime,
                IgnoredWindowTitlesText = NormalizeMultilineText(settings.Monitor.IgnoredWindowTitlesText) ?? string.Empty,
            },
            Delivery = settings.Delivery with
            {
                Mode = normalizedDeliveryMode,
            },
            Cloud = settings.Cloud with
            {
                BackendBaseUrl = NormalizeOptionalString(settings.Cloud.BackendBaseUrl),
            },
            Notifications = settings.Notifications with
            {
                DisconnectNotificationEnabled = settings.Notifications.DisconnectNotificationEnabled,
                GhostDisconnectMessageTemplate = disconnectTemplate,
                ClientDisconnectedMessageTemplate = disconnectTemplate,
                ProcessExitedNotificationEnabled = settings.Notifications.ProcessExitedNotificationEnabled,
                ProcessExitedMessageTemplate = NormalizeOptionalString(settings.Notifications.ProcessExitedMessageTemplate),
                DeadStartedNotificationEnabled = settings.Notifications.DeadStartedNotificationEnabled,
                DeadStartedMessageTemplate = NormalizeOptionalString(settings.Notifications.DeadStartedMessageTemplate),
            },
        };
    }

    private static IReadOnlyList<AgentConfigurationDiagnostic> BuildRuntimeDiagnostics(AgentConfigurationSnapshot snapshot)
    {
        var diagnostics = new List<AgentConfigurationDiagnostic>();

        if (string.Equals(snapshot.Settings.Delivery.Mode, "Local", StringComparison.OrdinalIgnoreCase))
        {
            if (!snapshot.Settings.Telegram.DeliveryEnabled || snapshot.Settings.Telegram.ChatId is null)
            {
                diagnostics.Add(new AgentConfigurationDiagnostic(
                    "delivery_startup_misconfigured",
                    "warning",
                    "Startup diagnostic: Telegram delivery mode is active but DeliveryEnabled or ChatId is missing."));
            }

            if (string.IsNullOrWhiteSpace(snapshot.Secrets.TelegramBotToken))
            {
                diagnostics.Add(new AgentConfigurationDiagnostic(
                    "delivery_startup_not_configured",
                    "warning",
                    "Startup diagnostic: Telegram delivery mode is active but no bot token is stored."));
            }
        }
        else if (string.Equals(snapshot.Settings.Delivery.Mode, "Cloud", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(snapshot.Settings.Cloud.BackendBaseUrl, UriKind.Absolute, out var backendUri)
                || (backendUri.Scheme != Uri.UriSchemeHttp && backendUri.Scheme != Uri.UriSchemeHttps))
            {
                diagnostics.Add(new AgentConfigurationDiagnostic(
                    "backend_startup_misconfigured",
                    "warning",
                    "Startup diagnostic: Cloud delivery mode is active but BackendBaseUrl is not a valid absolute http(s) URL."));
            }

            if (string.IsNullOrWhiteSpace(snapshot.Secrets.CloudAuthKey))
            {
                diagnostics.Add(new AgentConfigurationDiagnostic(
                    "backend_startup_not_configured",
                    "warning",
                    "Startup diagnostic: Cloud delivery mode is active but no auth key is stored."));
            }

        }

        return diagnostics;
    }

    private static string NormalizeDeliveryMode(string? mode)
    {
        if (string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase))
        {
            return "Local";
        }

        if (string.Equals(mode, "Cloud", StringComparison.OrdinalIgnoreCase))
        {
            return "Cloud";
        }

        if (string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            return "Disabled";
        }

        throw new AgentSettingsValidationException(
            $"Unsupported delivery mode '{mode}'. Expected Disabled, Local, or Cloud.");
    }

    private static string? NormalizeOptionalString(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeMultilineText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalizedLines = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return normalizedLines.Length == 0
            ? null
            : string.Join(Environment.NewLine, normalizedLines);
    }

    private sealed record LoadSnapshotResult(
        AgentConfigurationSnapshot Snapshot,
        IReadOnlyList<AgentConfigurationDiagnostic> Diagnostics,
        bool SkipSecretsPersistenceUntilExplicitRewrite,
        AgentConfigurationDiagnostic? PersistentSecretsLoadFailureDiagnostic);

    private sealed record LoadSecretsResult(
        AgentSecrets Secrets,
        bool SkipPersistenceUntilExplicitRewrite,
        AgentConfigurationDiagnostic? LoadFailureDiagnostic);
}

internal sealed record AgentConfigurationSnapshot(
    AgentSettings Settings,
    AgentSecrets Secrets);

internal sealed record AgentSettings
{
    public static AgentSettings Default { get; } = new();

    public LoopbackSettings Loopback { get; init; } = new();
    public AgentMonitorSettings Monitor { get; init; } = new();
    public DeliverySettings Delivery { get; init; } = new();
    public TelegramSettings Telegram { get; init; } = new();
    public CloudSettings Cloud { get; init; } = new();
    public AgentNotificationSettings Notifications { get; init; } = new();
}

internal sealed record LoopbackSettings
{
    public int Port { get; init; } = 45631;
}

internal sealed record AgentMonitorSettings
{
    public int PollIntervalMs { get; init; } = 30000;
    public int IdleTimeoutSec { get; init; } = 5;
    public int MinConfirmLifetimeSec { get; init; } = 15;
    public string IgnoredWindowTitlesText { get; init; } = string.Empty;
}

internal sealed record DeliverySettings
{
    public string Mode { get; init; } = "Disabled";
}

internal sealed record TelegramSettings
{
    public bool DeliveryEnabled { get; init; }
    public long? ChatId { get; init; }
}

internal sealed record CloudSettings
{
    public string? BackendBaseUrl { get; init; }
}

internal sealed record AgentNotificationSettings
{
    public bool DisconnectNotificationEnabled { get; init; } = true;
    public string? GhostDisconnectMessageTemplate { get; init; }
    public string? ClientDisconnectedMessageTemplate { get; init; }
    public bool ProcessExitedNotificationEnabled { get; init; } = true;
    public string? ProcessExitedMessageTemplate { get; init; }
    public bool DeadStartedNotificationEnabled { get; init; } = true;
    public string? DeadStartedMessageTemplate { get; init; }
}

internal sealed record AgentSecrets
{
    public static AgentSecrets Empty { get; } = new();

    public string LocalApiToken { get; init; } = string.Empty;
    public string TelegramBotToken { get; init; } = string.Empty;
    public string CloudAuthKey { get; init; } = string.Empty;
}

internal sealed class AgentSettingsValidationException(string message) : Exception(message);
