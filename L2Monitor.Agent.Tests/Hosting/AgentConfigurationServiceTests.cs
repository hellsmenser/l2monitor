using L2Monitor.Agent.Hosting;
using L2Monitor.Core.Api;
using L2Monitor.Core.Storage;
using System.Text.Json;
using Xunit;

namespace L2Monitor.Agent.Tests.Hosting;

public sealed class AgentConfigurationServiceTests
{
    [Fact]
    public void LoadPackagedDefaults_ReadsValidatedOfficialBackendUrl()
    {
        var root = Path.Combine(Path.GetTempPath(), "adenplus-defaults-tests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(
                Path.Combine(root, "adenplus.defaults.json"),
                "{\"backendBaseUrl\":\"https://aden.example/api\"}");

            var settings = AgentConfigurationService.LoadPackagedDefaults(root);

            Assert.Equal("https://aden.example/api", settings.Cloud.BackendBaseUrl?.TrimEnd('/'));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveRootDirectory_UsesInstalledManifestWhenEnvironmentOverrideIsMissing()
    {
        var appRoot = CreateTempAppRoot();
        var agentBaseDirectory = Path.Combine(appRoot, "agent");
        Directory.CreateDirectory(agentBaseDirectory);

        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");

        try
        {
            var expectedRoot = Path.Combine(Path.GetTempPath(), "l2monitor-agent-data", Guid.NewGuid().ToString("N"));
            File.WriteAllText(
                Path.Combine(appRoot, "install.json"),
                JsonSerializer.Serialize(new { agentDataRoot = expectedRoot }));
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", null);

            var resolvedRoot = AgentConfigurationService.ResolveRootDirectory(agentBaseDirectory);

            Assert.Equal(expectedRoot, resolvedRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            Directory.Delete(appRoot, recursive: true);
        }
    }

    [Fact]
    public void Constructor_WarnsWhenLoadedMonitorTimeoutValuesAreClamped()
    {
        var root = CreateTempAgentRoot();
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");

        try
        {
            var settings = AgentSettings.Default with
            {
                Monitor = AgentSettings.Default.Monitor with
                {
                    IdleTimeoutSec = 0,
                    MinConfirmLifetimeSec = -2,
                },
            };

            File.WriteAllText(
                Path.Combine(root, "settings.json"),
                JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true,
                }));

            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", root);

            var service = new AgentConfigurationService();

            var snapshot = service.GetSnapshot();
            Assert.Equal(1, snapshot.Settings.Monitor.IdleTimeoutSec);
            Assert.Equal(0, snapshot.Settings.Monitor.MinConfirmLifetimeSec);

            var diagnostics = service.GetDiagnostics();
            Assert.Contains(diagnostics, diagnostic =>
                diagnostic.Kind == "settings_idle_timeout_invalid" &&
                diagnostic.Severity == "warning" &&
                diagnostic.Summary == "Monitor.IdleTimeoutSec value '0' is out of range; using 1.");
            Assert.Contains(diagnostics, diagnostic =>
                diagnostic.Kind == "settings_min_confirm_lifetime_invalid" &&
                diagnostic.Severity == "warning" &&
                diagnostic.Summary == "Monitor.MinConfirmLifetimeSec value '-2' is out of range; using 0.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Constructor_UpgradesLegacyPersistedMinConfirmLifetimeDefault()
    {
        var root = CreateTempAgentRoot();
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");

        try
        {
            var settings = AgentSettings.Default with
            {
                Monitor = AgentSettings.Default.Monitor with
                {
                    MinConfirmLifetimeSec = 5,
                },
            };

            File.WriteAllText(
                Path.Combine(root, "settings.json"),
                JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true,
                }));

            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", root);

            var service = new AgentConfigurationService();

            Assert.Equal(15, service.GetSnapshot().Settings.Monitor.MinConfirmLifetimeSec);
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LoadBootstrapSettings_UpgradesLegacyPersistedMinConfirmLifetimeDefault()
    {
        var root = CreateTempAgentRoot();
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");

        try
        {
            var settings = AgentSettings.Default with
            {
                Monitor = AgentSettings.Default.Monitor with
                {
                    MinConfirmLifetimeSec = 5,
                },
            };

            File.WriteAllText(
                Path.Combine(root, "settings.json"),
                JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true,
                }));

            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", root);

            var loaded = AgentConfigurationService.LoadBootstrapSettings();

            Assert.Equal(15, loaded.Monitor.MinConfirmLifetimeSec);
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Constructor_DoesNotOverwriteUnreadableSecretsPayloadWhenTokenIsMissing()
    {
        var root = CreateTempAgentRoot();
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");
        var secretsPath = Path.Combine(root, "secrets.dat");
        var originalPayload = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };

        try
        {
            File.WriteAllBytes(secretsPath, originalPayload);
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", root);

            var service = new AgentConfigurationService();

            Assert.False(string.IsNullOrWhiteSpace(service.GetSnapshot().Secrets.LocalApiToken));
            Assert.True(service.IsRecoveryModeActive());
            Assert.Equal(originalPayload, File.ReadAllBytes(secretsPath));
            Assert.Contains(service.GetDiagnostics(), diagnostic => diagnostic.Kind == "secrets_load_failed" && diagnostic.Severity == "error");
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Constructor_PublishesRecoveryBootstrapWithoutLeakingGeneratedToken()
    {
        var root = CreateTempAgentRoot();
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");

        try
        {
            File.WriteAllBytes(Path.Combine(root, "secrets.dat"), [0x01, 0x02, 0x03]);
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", root);

            var service = new AgentConfigurationService();
            var bootstrap = AgentBootstrapStore.TryLoad(root);

            Assert.NotNull(service.GetSnapshot().Secrets.LocalApiToken);
            Assert.NotNull(bootstrap);
            Assert.Equal(AgentSettings.Default.Loopback.Port, bootstrap!.LoopbackPort);
            Assert.Null(bootstrap.LocalApiToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdateSettings_PublishesNormalizationDiagnosticsForInvalidApiValues()
    {
        var root = CreateTempAgentRoot();
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");

        try
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", root);
            var service = new AgentConfigurationService();

            var snapshot = service.UpdateSettings(new LocalControlSettingsDto(
                LoopbackPort: 1,
                PollIntervalMs: 100,
                IdleTimeoutSec: 0,
                MinConfirmLifetimeSec: -5,
                DeliveryMode: "Cloud",
                BackendBaseUrl: " https://backend.example.test ",
                TelegramDeliveryEnabled: false,
                TelegramChatId: null));

            Assert.Equal(AgentSettings.Default.Loopback.Port, snapshot.Settings.Loopback.Port);
            Assert.Equal(250, snapshot.Settings.Monitor.PollIntervalMs);
            Assert.Equal(1, snapshot.Settings.Monitor.IdleTimeoutSec);
            Assert.Equal(0, snapshot.Settings.Monitor.MinConfirmLifetimeSec);
            Assert.Equal("https://backend.example.test", snapshot.Settings.Cloud.BackendBaseUrl);

            var diagnostics = service.GetDiagnostics();
            Assert.Contains(diagnostics, diagnostic => diagnostic.Kind == "settings_loopback_port_invalid" && diagnostic.Severity == "warning");
            Assert.Contains(diagnostics, diagnostic => diagnostic.Kind == "settings_poll_interval_invalid" && diagnostic.Severity == "warning");
            Assert.Contains(diagnostics, diagnostic => diagnostic.Kind == "settings_idle_timeout_invalid" && diagnostic.Severity == "warning");
            Assert.Contains(diagnostics, diagnostic => diagnostic.Kind == "settings_min_confirm_lifetime_invalid" && diagnostic.Severity == "warning");
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdateSettings_PreservesPublishedBootstrapPortUntilRuntimeRefresh()
    {
        var root = CreateTempAgentRoot();
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");

        try
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", root);
            var service = new AgentConfigurationService();

            var initialBootstrap = AgentBootstrapStore.TryLoad(root);
            var initialToken = service.GetSnapshot().Secrets.LocalApiToken;

            Assert.NotNull(initialBootstrap);
            Assert.Equal(AgentSettings.Default.Loopback.Port, initialBootstrap!.LoopbackPort);
            Assert.Equal(initialToken, initialBootstrap.LocalApiToken);

            service.UpdateSettings(new LocalControlSettingsDto(
                LoopbackPort: 45632,
                PollIntervalMs: 1000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 0,
                DeliveryMode: "Disabled",
                BackendBaseUrl: null,
                TelegramDeliveryEnabled: false,
                TelegramChatId: null));

            var updatedBootstrap = AgentBootstrapStore.TryLoad(root);
            Assert.NotNull(updatedBootstrap);
            Assert.Equal(AgentSettings.Default.Loopback.Port, updatedBootstrap!.LoopbackPort);
            Assert.Equal(initialToken, updatedBootstrap.LocalApiToken);

            service.RefreshBootstrapSnapshot(45631);

            var refreshedBootstrap = AgentBootstrapStore.TryLoad(root);
            Assert.NotNull(refreshedBootstrap);
            Assert.Equal(45631, refreshedBootstrap!.LoopbackPort);
            Assert.Equal(initialToken, refreshedBootstrap.LocalApiToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdateSettings_PersistsNotificationTemplatesAndIgnoredWindows()
    {
        var root = CreateTempAgentRoot();
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");

        try
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", root);
            var service = new AgentConfigurationService();

            var snapshot = service.UpdateSettings(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 1000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 0,
                DeliveryMode: "Local",
                BackendBaseUrl: null,
                TelegramDeliveryEnabled: true,
                TelegramChatId: 123,
                IgnoredWindowTitlesText: " splash \r\n\r\n updater ",
                DisconnectNotificationEnabled: false,
                GhostDisconnectMessageTemplate: " ghost {pid} ",
                ClientDisconnectedMessageTemplate: " disc {name} ",
                ProcessExitedNotificationEnabled: false,
                ProcessExitedMessageTemplate: " exit {windowTitle} ",
                DeadStartedNotificationEnabled: true,
                DeadStartedMessageTemplate: " dead {name} "));

            Assert.Equal("splash" + Environment.NewLine + "updater", snapshot.Settings.Monitor.IgnoredWindowTitlesText);
            Assert.False(snapshot.Settings.Notifications.DisconnectNotificationEnabled);
            Assert.Equal("ghost {pid}", snapshot.Settings.Notifications.GhostDisconnectMessageTemplate);
            Assert.Equal("ghost {pid}", snapshot.Settings.Notifications.ClientDisconnectedMessageTemplate);
            Assert.False(snapshot.Settings.Notifications.ProcessExitedNotificationEnabled);
            Assert.Equal("exit {windowTitle}", snapshot.Settings.Notifications.ProcessExitedMessageTemplate);
            Assert.True(snapshot.Settings.Notifications.DeadStartedNotificationEnabled);
            Assert.Equal("dead {name}", snapshot.Settings.Notifications.DeadStartedMessageTemplate);
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UpdateTraySettings_PreservesNonTrayOwnedSettings()
    {
        var root = CreateTempAgentRoot();
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");

        try
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", root);
            var service = new AgentConfigurationService();

            service.UpdateSettings(new LocalControlSettingsDto(
                LoopbackPort: 45632,
                PollIntervalMs: 2000,
                IdleTimeoutSec: 7,
                MinConfirmLifetimeSec: 3,
                DeliveryMode: "Cloud",
                BackendBaseUrl: "https://backend.example.test",
                TelegramDeliveryEnabled: true,
                TelegramChatId: 321,
                IgnoredWindowTitlesText: "splash",
                GhostDisconnectMessageTemplate: "ghost old",
                ClientDisconnectedMessageTemplate: "disc hidden",
                ProcessExitedMessageTemplate: "exit old",
                DeadStartedMessageTemplate: "dead old"));

            var snapshot = service.UpdateTraySettings(new TrayEditableSettingsDto(
                DeliveryMode: "Local",
                TelegramDeliveryEnabled: false,
                IgnoredWindowTitlesText: " updater \r\n helper ",
                DisconnectNotificationEnabled: false,
                GhostDisconnectMessageTemplate: "ghost new",
                ProcessExitedNotificationEnabled: true,
                ProcessExitedMessageTemplate: "exit new",
                DeadStartedNotificationEnabled: false,
                DeadStartedMessageTemplate: "dead new"));

            Assert.Equal(45632, snapshot.Settings.Loopback.Port);
            Assert.Equal(2000, snapshot.Settings.Monitor.PollIntervalMs);
            Assert.Equal(7, snapshot.Settings.Monitor.IdleTimeoutSec);
            Assert.Equal(3, snapshot.Settings.Monitor.MinConfirmLifetimeSec);
            Assert.Equal("https://backend.example.test", snapshot.Settings.Cloud.BackendBaseUrl);
            Assert.Equal(321, snapshot.Settings.Telegram.ChatId);
            Assert.Equal("Local", snapshot.Settings.Delivery.Mode);
            Assert.False(snapshot.Settings.Telegram.DeliveryEnabled);
            Assert.Equal("updater" + Environment.NewLine + "helper", snapshot.Settings.Monitor.IgnoredWindowTitlesText);
            Assert.False(snapshot.Settings.Notifications.DisconnectNotificationEnabled);
            Assert.Equal("ghost new", snapshot.Settings.Notifications.GhostDisconnectMessageTemplate);
            Assert.Equal("ghost new", snapshot.Settings.Notifications.ClientDisconnectedMessageTemplate);
            Assert.True(snapshot.Settings.Notifications.ProcessExitedNotificationEnabled);
            Assert.Equal("exit new", snapshot.Settings.Notifications.ProcessExitedMessageTemplate);
            Assert.False(snapshot.Settings.Notifications.DeadStartedNotificationEnabled);
            Assert.Equal("dead new", snapshot.Settings.Notifications.DeadStartedMessageTemplate);
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RepeatedSettingsAndSecretsUpdates_ReplaceExistingFilesWithoutLeavingTempArtifacts()
    {
        var root = CreateTempAgentRoot();
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");

        try
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", root);
            var service = new AgentConfigurationService();

            service.UpdateSettings(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 1000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 0,
                DeliveryMode: "Disabled",
                BackendBaseUrl: null,
                TelegramDeliveryEnabled: false,
                TelegramChatId: null));
            service.UpdateTelegramSecret("bot-one");
            service.UpdateCloudSecret("cloud-one");

            service.UpdateSettings(new LocalControlSettingsDto(
                LoopbackPort: 45632,
                PollIntervalMs: 2000,
                IdleTimeoutSec: 7,
                MinConfirmLifetimeSec: 3,
                DeliveryMode: "Cloud",
                BackendBaseUrl: "https://backend.example.test",
                TelegramDeliveryEnabled: true,
                TelegramChatId: 321,
                IgnoredWindowTitlesText: " splash ",
                DisconnectNotificationEnabled: false,
                GhostDisconnectMessageTemplate: "ghost {pid}",
                ClientDisconnectedMessageTemplate: "disc {name}",
                ProcessExitedNotificationEnabled: true,
                ProcessExitedMessageTemplate: "exit {windowTitle}",
                DeadStartedNotificationEnabled: false,
                DeadStartedMessageTemplate: "dead {name}"));
            service.UpdateTelegramSecret("bot-two");
            service.UpdateCloudSecret("cloud-two");

            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp", SearchOption.TopDirectoryOnly));

            var reloaded = new AgentConfigurationService().GetSnapshot();
            Assert.Equal(45632, reloaded.Settings.Loopback.Port);
            Assert.Equal(2000, reloaded.Settings.Monitor.PollIntervalMs);
            Assert.Equal(7, reloaded.Settings.Monitor.IdleTimeoutSec);
            Assert.Equal(3, reloaded.Settings.Monitor.MinConfirmLifetimeSec);
            Assert.Equal("Cloud", reloaded.Settings.Delivery.Mode);
            Assert.Equal("https://backend.example.test", reloaded.Settings.Cloud.BackendBaseUrl);
            Assert.True(reloaded.Settings.Telegram.DeliveryEnabled);
            Assert.Equal(321, reloaded.Settings.Telegram.ChatId);
            Assert.Equal("splash", reloaded.Settings.Monitor.IgnoredWindowTitlesText);
            Assert.False(reloaded.Settings.Notifications.DisconnectNotificationEnabled);
            Assert.Equal("ghost {pid}", reloaded.Settings.Notifications.GhostDisconnectMessageTemplate);
            Assert.Equal("ghost {pid}", reloaded.Settings.Notifications.ClientDisconnectedMessageTemplate);
            Assert.True(reloaded.Settings.Notifications.ProcessExitedNotificationEnabled);
            Assert.Equal("exit {windowTitle}", reloaded.Settings.Notifications.ProcessExitedMessageTemplate);
            Assert.False(reloaded.Settings.Notifications.DeadStartedNotificationEnabled);
            Assert.Equal("dead {name}", reloaded.Settings.Notifications.DeadStartedMessageTemplate);
            Assert.Equal("bot-two", reloaded.Secrets.TelegramBotToken);
            Assert.Equal("cloud-two", reloaded.Secrets.CloudAuthKey);
            Assert.False(string.IsNullOrWhiteSpace(reloaded.Secrets.LocalApiToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempAppRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "l2monitor-agent-config-tests", Guid.NewGuid().ToString("N"), "app");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateTempAgentRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "l2monitor-agent-config-tests", Guid.NewGuid().ToString("N"), "agent");
        Directory.CreateDirectory(root);
        return root;
    }
}
