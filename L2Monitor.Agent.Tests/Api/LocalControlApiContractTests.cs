using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using L2Monitor.Agent.Hosting;
using L2Monitor.Agent.Runtime;
using L2Monitor.Agent.Updates;
using L2Monitor.Core.Api;
using L2Monitor.Core.Delivery;
using L2Monitor.Core.Models;
using L2Monitor.Core.Storage;
using L2Monitor.Infrastructure.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace L2Monitor.Agent.Tests.Api;

public sealed class LocalControlApiContractTests
{
    private static readonly byte[] SecretEntropy = Encoding.UTF8.GetBytes("L2Monitor.Agent.SecretStore.v1");

    [Fact]
    public async Task Status_RequiresBearerToken()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using var response = await scope.Client.GetAsync("/v1/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("unauthorized", payload.Error);
    }

    [Fact]
    public async Task Status_ReturnsVersionedContractHeadersAndBody()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status");
        using var response = await scope.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal(LocalControlApiContract.CurrentVersion, response.Headers.GetValues(LocalControlApiContract.VersionHeaderName).Single());
        Assert.Equal(LocalControlApiContract.MinimumSupportedVersion, response.Headers.GetValues(LocalControlApiContract.MinimumVersionHeaderName).Single());

        var payload = await response.Content.ReadFromJsonAsync<StatusResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(LocalControlApiContract.CurrentVersion, payload.ApiVersion.Current);
        Assert.Equal("Disabled", payload.CurrentMode);
        Assert.True(payload.ActiveLoopbackPort > 0);
        Assert.True(payload.ProbeCount >= 1);
        Assert.NotNull(payload.Delivery);
        Assert.NotNull(payload.Backend);
    }

    [Fact]
    public async Task Status_ExposesAvailableReleaseForTrayBannerAndWindowsNotification()
    {
        await using var scope = await AgentHostScope.CreateAsync();
        scope.Host.Services.GetRequiredService<AgentControlStateStore>().SetLastUpdate(
            new AgentUpdateStateRecord(
                "available",
                "1.0.1",
                "1.0.2",
                true,
                true,
                "https://github.com/hellsmenser/l2monitor/releases/tag/v1.0.2",
                DateTimeOffset.UtcNow));

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status");
        using var response = await scope.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<StatusResponseDto>();
        Assert.NotNull(payload?.Update);
        Assert.True(payload.Update.IsUpdateAvailable);
        Assert.True(payload.Update.Required);
        Assert.Equal("1.0.2", payload.Update.LatestVersion);
        Assert.StartsWith("https://github.com/hellsmenser/l2monitor/releases/", payload.Update.ReleaseUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("Cloud")]
    public async Task UpdateCheck_IsActiveInAutonomousAndServiceDeliveryModes(string deliveryMode)
    {
        var initialSettings = JsonSerializer.Serialize(AgentSettings.Default with
        {
            Delivery = new DeliverySettings { Mode = deliveryMode },
            Cloud = new CloudSettings { BackendBaseUrl = "https://service.example/" },
        });
        await using var scope = await AgentHostScope.CreateAsync(
            builder =>
            {
                builder.Services.RemoveAll<IAgentReleaseUpdateChecker>();
                builder.Services.AddSingleton<IAgentReleaseUpdateChecker, StubReleaseUpdateChecker>();
            },
            initialSettingsJson: initialSettings);

        StatusResponseDto? payload = null;
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < timeout)
        {
            using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status");
            using var response = await scope.Client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            payload = await response.Content.ReadFromJsonAsync<StatusResponseDto>();
            if (payload?.Update?.IsUpdateAvailable == true)
            {
                break;
            }
            await Task.Delay(10);
        }

        Assert.Equal(deliveryMode, payload?.CurrentMode);
        Assert.True(payload?.Update?.IsUpdateAvailable);
    }

    [Fact]
    public async Task Status_ExposesExplicitCurrentMode()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using (var updateRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            updateRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 30000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 5,
                DeliveryMode: "Cloud",
                BackendBaseUrl: "https://backend.example.test",
                TelegramDeliveryEnabled: false,
                TelegramChatId: null)));

            using var updateResponse = await scope.Client.SendAsync(updateRequest);
            updateResponse.EnsureSuccessStatusCode();
        }

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status");
        using var response = await scope.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<StatusResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("Cloud", payload.CurrentMode);
    }

    [Fact]
    public async Task Requests_RejectUnsupportedMajorVersion()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status");
        request.Headers.Add(LocalControlApiContract.VersionHeaderName, "2.0");

        using var response = await scope.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("unsupported_api_version", payload.Error);
    }

    [Fact]
    public async Task Settings_RoundTripUsesStableDtoShapeAndMasksSecrets()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        var update = new UpdateSettingsRequestDto(new LocalControlSettingsDto(
            LoopbackPort: 45642,
            PollIntervalMs: 1500,
            IdleTimeoutSec: 7,
            MinConfirmLifetimeSec: 2,
            DeliveryMode: "Cloud",
            BackendBaseUrl: "https://backend.example.test",
            TelegramDeliveryEnabled: true,
            TelegramChatId: 1234567890));

        using (var updateRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            updateRequest.Content = JsonContent.Create(update);
            using var updateResponse = await scope.Client.SendAsync(updateRequest);
            updateResponse.EnsureSuccessStatusCode();
        }

        using (var secretRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/settings/secrets/cloud"))
        {
            secretRequest.Content = JsonContent.Create(new UpdateCloudSecretRequestDto("secret-key"));
            using var secretResponse = await scope.Client.SendAsync(secretRequest);
            secretResponse.EnsureSuccessStatusCode();
        }

        using var getRequest = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/settings");
        using var getResponse = await scope.Client.SendAsync(getRequest);
        getResponse.EnsureSuccessStatusCode();

        var payload = await getResponse.Content.ReadFromJsonAsync<SettingsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(scope.BaseAddress.Port, payload.ActiveLoopbackPort);
        Assert.Equal(45642, payload.Settings.LoopbackPort);
        Assert.Equal("Cloud", payload.Settings.DeliveryMode);
        Assert.Equal("https://backend.example.test", payload.Settings.BackendBaseUrl);
        Assert.True(payload.Secrets.HasLocalApiToken);
        Assert.False(payload.Secrets.HasTelegramBotToken);
        Assert.True(payload.Secrets.HasCloudAuthKey);
    }

    [Fact]
    public async Task Settings_UpdateRejectsUnsupportedDeliveryMode()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings");
        request.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
            LoopbackPort: 45631,
            PollIntervalMs: 30000,
            IdleTimeoutSec: 5,
            MinConfirmLifetimeSec: 5,
            DeliveryMode: "FaxMachine",
            BackendBaseUrl: null,
            TelegramDeliveryEnabled: false,
            TelegramChatId: null)));

        using var response = await scope.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<ErrorResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("invalid_settings", payload.Error);
        Assert.Contains("Unsupported delivery mode", payload.Message, StringComparison.Ordinal);

        using var getRequest = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/settings");
        using var getResponse = await scope.Client.SendAsync(getRequest);
        getResponse.EnsureSuccessStatusCode();
        var getPayload = await getResponse.Content.ReadFromJsonAsync<SettingsResponseDto>();
        Assert.NotNull(getPayload);
        Assert.Equal("Disabled", getPayload.Settings.DeliveryMode);
    }

    [Fact]
    public async Task Settings_ExposeConfiguredAndActiveLoopbackPortsSeparately()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings");
        request.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
            LoopbackPort: 45642,
            PollIntervalMs: 30000,
            IdleTimeoutSec: 5,
            MinConfirmLifetimeSec: 5,
            DeliveryMode: "Disabled",
            BackendBaseUrl: null,
            TelegramDeliveryEnabled: false,
            TelegramChatId: null)));

        using var response = await scope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<SettingsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(45642, payload.Settings.LoopbackPort);
        Assert.Equal(scope.BaseAddress.Port, payload.ActiveLoopbackPort);
        Assert.NotEqual(payload.Settings.LoopbackPort, payload.ActiveLoopbackPort);
    }

    [Fact]
    public async Task Settings_UpdateKeepsPublishedBootstrapOnActiveLoopbackPort()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings");
        request.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
            LoopbackPort: 45642,
            PollIntervalMs: 30000,
            IdleTimeoutSec: 5,
            MinConfirmLifetimeSec: 5,
            DeliveryMode: "Disabled",
            BackendBaseUrl: null,
            TelegramDeliveryEnabled: false,
            TelegramChatId: null)));

        using var response = await scope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var bootstrap = AgentBootstrapStore.TryLoad(scope.RootDirectory);
        Assert.NotNull(bootstrap);
        Assert.Equal(scope.BaseAddress.Port, bootstrap!.LoopbackPort);
        Assert.NotEqual(45642, bootstrap.LoopbackPort);
        Assert.Equal(scope.Token, bootstrap.LocalApiToken);
    }

    [Fact]
    public async Task TraySettingsUpdate_PreservesNonTrayOwnedSettings()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using (var seedRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            seedRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 45642,
                PollIntervalMs: 1500,
                IdleTimeoutSec: 7,
                MinConfirmLifetimeSec: 2,
                DeliveryMode: "Cloud",
                BackendBaseUrl: "https://backend.example.test",
                TelegramDeliveryEnabled: true,
                TelegramChatId: 1234567890,
                IgnoredWindowTitlesText: "seed",
                DisconnectNotificationEnabled: true,
                GhostDisconnectMessageTemplate: "ghost old",
                ClientDisconnectedMessageTemplate: "disc hidden",
                ProcessExitedNotificationEnabled: true,
                ProcessExitedMessageTemplate: "exit old",
                DeadStartedNotificationEnabled: true,
                DeadStartedMessageTemplate: "dead old")));

            using var seedResponse = await scope.Client.SendAsync(seedRequest);
            seedResponse.EnsureSuccessStatusCode();
        }

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings/tray");
        request.Content = JsonContent.Create(new UpdateTraySettingsRequestDto(new TrayEditableSettingsDto(
            DeliveryMode: "Local",
            TelegramDeliveryEnabled: false,
            IgnoredWindowTitlesText: " splash \r\n updater ",
            DisconnectNotificationEnabled: false,
            GhostDisconnectMessageTemplate: "ghost new",
            ProcessExitedNotificationEnabled: true,
            ProcessExitedMessageTemplate: "exit new",
            DeadStartedNotificationEnabled: false,
            DeadStartedMessageTemplate: "dead new")));

        using var response = await scope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<SettingsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(45642, payload.Settings.LoopbackPort);
        Assert.Equal(1500, payload.Settings.PollIntervalMs);
        Assert.Equal(7, payload.Settings.IdleTimeoutSec);
        Assert.Equal(2, payload.Settings.MinConfirmLifetimeSec);
        Assert.Equal("Local", payload.Settings.DeliveryMode);
        Assert.Equal("https://backend.example.test", payload.Settings.BackendBaseUrl);
        Assert.False(payload.Settings.TelegramDeliveryEnabled);
        Assert.Equal(1234567890, payload.Settings.TelegramChatId);
        Assert.Equal("splash" + Environment.NewLine + "updater", payload.Settings.IgnoredWindowTitlesText);
        Assert.False(payload.Settings.DisconnectNotificationEnabled);
        Assert.Equal("ghost new", payload.Settings.GhostDisconnectMessageTemplate);
        Assert.Equal("ghost new", payload.Settings.ClientDisconnectedMessageTemplate);
        Assert.True(payload.Settings.ProcessExitedNotificationEnabled);
        Assert.Equal("exit new", payload.Settings.ProcessExitedMessageTemplate);
        Assert.False(payload.Settings.DeadStartedNotificationEnabled);
        Assert.Equal("dead new", payload.Settings.DeadStartedMessageTemplate);
    }

    [Fact]
    public async Task TraySettingsUpdate_DeserializesCanonicalTrayDtoShape()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings/tray");
        request.Content = JsonContent.Create(new
        {
            settings = new
            {
                deliveryMode = "Local",
                telegramDeliveryEnabled = true,
                ignoredWindowTitlesText = " updater ",
                disconnectNotificationEnabled = false,
                ghostDisconnectMessageTemplate = "ghost",
                processExitedNotificationEnabled = false,
                processExitedMessageTemplate = "exit",
                deadStartedNotificationEnabled = true,
                deadStartedMessageTemplate = "dead"
            }
        });

        using var response = await scope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<SettingsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("Local", payload.Settings.DeliveryMode);
        Assert.True(payload.Settings.TelegramDeliveryEnabled);
        Assert.Equal("updater", payload.Settings.IgnoredWindowTitlesText);
        Assert.False(payload.Settings.DisconnectNotificationEnabled);
        Assert.Equal("ghost", payload.Settings.GhostDisconnectMessageTemplate);
        Assert.False(payload.Settings.ProcessExitedNotificationEnabled);
        Assert.Equal("exit", payload.Settings.ProcessExitedMessageTemplate);
        Assert.True(payload.Settings.DeadStartedNotificationEnabled);
        Assert.Equal("dead", payload.Settings.DeadStartedMessageTemplate);
    }

    [Fact]
    public async Task TraySettingsUpdate_PreservesUtf8DisconnectTemplateAndRestoresDefaultsWhenCleared()
    {
        await using var scope = await AgentHostScope.CreateAsync();
        const string exactTemplate = "Дисконнект у {name} ⚔️";

        using (var saveRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings/tray"))
        {
            saveRequest.Content = JsonContent.Create(new UpdateTraySettingsRequestDto(new TrayEditableSettingsDto(
                DeliveryMode: "Local",
                TelegramDeliveryEnabled: false,
                IgnoredWindowTitlesText: null,
                DisconnectNotificationEnabled: true,
                GhostDisconnectMessageTemplate: exactTemplate,
                ProcessExitedNotificationEnabled: true,
                ProcessExitedMessageTemplate: null,
                DeadStartedNotificationEnabled: true,
                DeadStartedMessageTemplate: null)));

            using var saveResponse = await scope.Client.SendAsync(saveRequest);
            saveResponse.EnsureSuccessStatusCode();
            var payload = await saveResponse.Content.ReadFromJsonAsync<SettingsResponseDto>();
            Assert.NotNull(payload);
            Assert.Equal(exactTemplate, payload.Settings.GhostDisconnectMessageTemplate);
            Assert.Equal(exactTemplate, payload.Settings.ClientDisconnectedMessageTemplate);
        }

        var settingsJson = await File.ReadAllTextAsync(Path.Combine(scope.RootDirectory, "settings.json"), Encoding.UTF8);
        Assert.Contains(exactTemplate, settingsJson, StringComparison.Ordinal);
        var reloaded = new AgentConfigurationService().GetSnapshot();
        Assert.Equal(exactTemplate, reloaded.Settings.Notifications.GhostDisconnectMessageTemplate);
        Assert.Equal(exactTemplate, reloaded.Settings.Notifications.ClientDisconnectedMessageTemplate);

        using var clearRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings/tray");
        clearRequest.Content = JsonContent.Create(new UpdateTraySettingsRequestDto(new TrayEditableSettingsDto(
            DeliveryMode: "Local",
            TelegramDeliveryEnabled: false,
            IgnoredWindowTitlesText: null,
            DisconnectNotificationEnabled: true,
            GhostDisconnectMessageTemplate: null,
            ProcessExitedNotificationEnabled: true,
            ProcessExitedMessageTemplate: null,
            DeadStartedNotificationEnabled: true,
            DeadStartedMessageTemplate: null)));

        using var clearResponse = await scope.Client.SendAsync(clearRequest);
        clearResponse.EnsureSuccessStatusCode();
        var cleared = new AgentConfigurationService().GetSnapshot();
        Assert.Equal(AgentNotificationDefaults.DisconnectMessageTemplate, cleared.Settings.Notifications.GhostDisconnectMessageTemplate);
        Assert.Equal(AgentNotificationDefaults.DisconnectMessageTemplate, cleared.Settings.Notifications.ClientDisconnectedMessageTemplate);
        Assert.Equal(AgentNotificationDefaults.ProcessExitedMessageTemplate, cleared.Settings.Notifications.ProcessExitedMessageTemplate);
        Assert.Equal(AgentNotificationDefaults.DeadStartedMessageTemplate, cleared.Settings.Notifications.DeadStartedMessageTemplate);
        Assert.DoesNotContain(exactTemplate, await File.ReadAllTextAsync(Path.Combine(scope.RootDirectory, "settings.json"), Encoding.UTF8), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TelegramSecretUpdate_PersistsPresenceWithoutLeakingSecret()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        const string secret = "telegram-bot-token";
        using var request = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/settings/secrets/telegram");
        request.Content = JsonContent.Create(new UpdateTelegramSecretRequestDto(secret));

        using var response = await scope.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        var rawBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, rawBody, StringComparison.Ordinal);

        var payload = await response.Content.ReadFromJsonAsync<SettingsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(scope.BaseAddress.Port, payload.ActiveLoopbackPort);
        Assert.True(payload.Secrets.HasTelegramBotToken);
        Assert.True(payload.Secrets.HasLocalApiToken);
        Assert.Equal("Disabled", payload.Settings.DeliveryMode);
    }

    [Fact]
    public async Task Incidents_ReturnsLimitedStableDtoShape()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        var controlState = scope.Host.Services.GetRequiredService<AgentControlStateStore>();
        var now = DateTimeOffset.Parse("2026-07-01T12:00:00+00:00");
        controlState.RecordIncident("probe_started", "info", "Probe started.", now);
        controlState.RecordIncident("delivery_failed", "warning", "Delivery failed.", now.AddSeconds(1));
        controlState.RecordIncident("recovered", "info", "Connection recovered.", now.AddSeconds(2));

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/incidents?limit=2");
        using var response = await scope.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<IncidentsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(LocalControlApiContract.CurrentVersion, payload.ApiVersion.Current);
        Assert.Equal(2, payload.Items.Count);
        Assert.Equal("recovered", payload.Items[0].Kind);
        Assert.Equal("delivery_failed", payload.Items[1].Kind);
        Assert.All(payload.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.Id)));
    }

    [Fact]
    public async Task Reload_ReturnsActionContractAndRecordsIncident()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/actions/reload");
        using var response = await scope.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("reload", payload.Result.Action);
        Assert.Equal("completed", payload.Result.Outcome);
        Assert.Equal("Configuration reloaded from disk.", payload.Result.Message);

        using var incidentsRequest = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/incidents?limit=1");
        using var incidentsResponse = await scope.Client.SendAsync(incidentsRequest);
        incidentsResponse.EnsureSuccessStatusCode();

        var incidents = await incidentsResponse.Content.ReadFromJsonAsync<IncidentsResponseDto>();
        Assert.NotNull(incidents);
        Assert.Single(incidents.Items);
        Assert.Equal("configuration_reloaded", incidents.Items[0].Kind);
    }

    [Fact]
    public async Task Startup_BrokenSettingsFileRecordsDiagnosticIncident()
    {
        await using var scope = await AgentHostScope.CreateAsync(
            initialSettingsJson: "{ this is not valid json");

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/incidents?limit=5");
        using var response = await scope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<IncidentsResponseDto>();
        Assert.NotNull(payload);
        Assert.Contains(payload.Items, item => item.Kind == "settings_load_failed" && item.Severity == "error");
    }

    [Fact]
    public async Task Settings_UpdateNormalizationRecordsDiagnosticIncidents()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using (var request = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            request.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 99,
                PollIntervalMs: 100,
                IdleTimeoutSec: 0,
                MinConfirmLifetimeSec: -1,
                DeliveryMode: "Disabled",
                BackendBaseUrl: null,
                TelegramDeliveryEnabled: false,
                TelegramChatId: null)));

            using var response = await scope.Client.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }

        using var incidentsRequest = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/incidents?limit=10");
        using var incidentsResponse = await scope.Client.SendAsync(incidentsRequest);
        incidentsResponse.EnsureSuccessStatusCode();

        var payload = await incidentsResponse.Content.ReadFromJsonAsync<IncidentsResponseDto>();
        Assert.NotNull(payload);
        Assert.Contains(payload.Items, item => item.Kind == "settings_loopback_port_invalid" && item.Severity == "warning");
        Assert.Contains(payload.Items, item => item.Kind == "settings_poll_interval_invalid" && item.Severity == "warning");
        Assert.Contains(payload.Items, item => item.Kind == "settings_idle_timeout_invalid" && item.Severity == "warning");
        Assert.Contains(payload.Items, item => item.Kind == "settings_min_confirm_lifetime_invalid" && item.Severity == "warning");
    }

    [Fact]
    public async Task RestartAgent_ReturnsAcceptedContractAndStopsHost()
    {
        await using var scope = await AgentHostScope.CreateAsync(builder =>
        {
            builder.Services.RemoveAll<IAgentRestartCoordinator>();
            builder.Services.AddSingleton<IAgentRestartCoordinator>(
                new StubRestartCoordinator(new AgentRestartDecision(
                    ShouldStop: true,
                    Outcome: "accepted",
                    Message: "restart queued")));
        });
        var lifetime = scope.Host.Services.GetRequiredService<IHostApplicationLifetime>();

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/actions/restart-agent");
        using var response = await scope.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("restart-agent", payload.Result.Action);
        Assert.Equal("accepted", payload.Result.Outcome);
        Assert.True(lifetime.ApplicationStopping.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RestartAgent_ReturnsRejectedContractWithoutStoppingHostWhenReplacementLaunchFails()
    {
        await using var scope = await AgentHostScope.CreateAsync(builder =>
        {
            builder.Services.RemoveAll<IAgentRestartCoordinator>();
            builder.Services.AddSingleton<IAgentRestartCoordinator>(
                new StubRestartCoordinator(new AgentRestartDecision(
                    ShouldStop: false,
                    Outcome: "rejected",
                    Message: "restart unavailable")));
        });
        var lifetime = scope.Host.Services.GetRequiredService<IHostApplicationLifetime>();

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/actions/restart-agent");
        using var response = await scope.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("restart-agent", payload.Result.Action);
        Assert.Equal("rejected", payload.Result.Outcome);
        Assert.False(lifetime.ApplicationStopping.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(250)));
    }

    [Fact]
    public async Task Shutdown_ReturnsAcceptedContractAndStopsHost()
    {
        await using var scope = await AgentHostScope.CreateAsync();
        var lifetime = scope.Host.Services.GetRequiredService<IHostApplicationLifetime>();

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/actions/shutdown");
        using var response = await scope.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("shutdown", payload.Result.Action);
        Assert.Equal("accepted", payload.Result.Outcome);
        Assert.True(lifetime.ApplicationStopping.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Connections_ReturnsExportedRuntimeConnectionRecords()
    {
        var capturedAt = DateTimeOffset.Parse("2026-07-01T09:15:00+00:00");
        await using var scope = await AgentHostScope.CreateAsync(builder =>
        {
            builder.Services.RemoveAll<IAgentRuntimeProbe>();
            builder.Services.AddSingleton<IAgentRuntimeProbe>(new StubRuntimeProbe(new AgentRuntimeProbeSnapshot(
                capturedAt,
                CandidateProcessCount: 2,
                GameConnectionCount: 2,
                Connections:
                [
                    new AgentRuntimeConnectionSnapshot(
                        "conn-1",
                        "l2.exe",
                        "Lineage II",
                        "GameplayOnly",
                        capturedAt,
                        new AgentConnectionForensics(
                            "gameplay_only",
                            "Gameplay socket observed without session socket.",
                            1,
                            [7777])),
                    new AgentRuntimeConnectionSnapshot("conn-2", "l2.bin", null, "reconnecting", capturedAt.AddSeconds(1)),
                ])));
        });

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/connections");
        using var response = await scope.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ConnectionsResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(2, payload.Items.Count);
        Assert.Equal("conn-1", payload.Items[0].Id);
        Assert.Equal("l2.exe", payload.Items[0].ProcessName);
        Assert.Equal("Lineage II", payload.Items[0].WindowTitle);
        Assert.Equal("GameplayOnly", payload.Items[0].State);
        var forensics = Assert.IsType<AgentConnectionForensicsDto>(payload.Items[0].Forensics);
        Assert.Equal("gameplay_only", forensics.Kind);
        Assert.Equal([7777], forensics.ObservedRemotePorts);
        Assert.Equal("conn-2", payload.Items[1].Id);
        Assert.Equal("reconnecting", payload.Items[1].State);
    }

    [Fact]
    public async Task Status_UsesActiveConnectionCountWithoutDroppingObservedRows()
    {
        await using var scope = await AgentHostScope.CreateAsync();
        var runtimeState = scope.Host.Services.GetRequiredService<AgentRuntimeStateStore>();
        var startedAt = DateTimeOffset.Parse("2026-07-01T09:00:00+00:00");
        var observedAt = DateTimeOffset.Parse("2026-07-01T09:15:00+00:00");

        runtimeState.Update(new AgentRuntimeState(
            AgentRuntimeStatus.Running,
            startedAt,
            observedAt,
            null,
            ProbeCount: 7,
            LastObservedProcessCount: 3,
            LastObservedConnectionCount: 2,
            Connections:
            [
                new AgentConnectionRecord("runtime-1", "l2.exe", "Lineage II", "Connected", observedAt),
                new AgentConnectionRecord("runtime-2", "l2.exe", "Lineage II", "Connecting", observedAt),
                new AgentConnectionRecord(
                    "runtime-3",
                    "l2.exe",
                    "Lineage II",
                    "GameplayOnly",
                    observedAt,
                    new AgentConnectionForensics(
                        "gameplay_only",
                        "Gameplay socket observed without session socket.",
                        1,
                        [7777])),
            ]));

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status");
        using var response = await scope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<StatusResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(2, payload.ActiveConnectionCount);
        Assert.Equal(3, payload.Connections.Count);
        Assert.Equal("runtime-1", payload.Connections[0].Id);
        Assert.Equal("GameplayOnly", payload.Connections[2].State);
        Assert.NotNull(payload.Connections[2].Forensics);
    }

    [Fact]
    public async Task Status_RecomputesDeliveryHealthAfterSettingsChange()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using (var settingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            settingsRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 30000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 5,
                DeliveryMode: "Local",
                BackendBaseUrl: null,
                TelegramDeliveryEnabled: true,
                TelegramChatId: 1234567890)));

            using var settingsResponse = await scope.Client.SendAsync(settingsRequest);
            settingsResponse.EnsureSuccessStatusCode();
        }

        using (var secretRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/settings/secrets/telegram"))
        {
            secretRequest.Content = JsonContent.Create(new UpdateTelegramSecretRequestDto("telegram-bot-token"));
            using var secretResponse = await scope.Client.SendAsync(secretRequest);
            secretResponse.EnsureSuccessStatusCode();
        }

        var controlState = scope.Host.Services.GetRequiredService<AgentControlStateStore>();
        controlState.SetLastDeliveryHealth(new DeliveryHealthSnapshot(
            DeliveryMode.Local,
            DeliveryHealthState.Healthy,
            "Telegram delivered successfully.",
            DateTimeOffset.Parse("2026-07-01T10:30:00+00:00"),
            DateTimeOffset.Parse("2026-07-01T10:30:00+00:00")));

        using (var disableRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            disableRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 30000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 5,
                DeliveryMode: "Disabled",
                BackendBaseUrl: null,
                TelegramDeliveryEnabled: false,
                TelegramChatId: null)));

            using var disableResponse = await scope.Client.SendAsync(disableRequest);
            disableResponse.EnsureSuccessStatusCode();
        }

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status");
        using var response = await scope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<StatusResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("Disabled", payload.CurrentMode);
        Assert.Equal("disabled", payload.Delivery.State);
        Assert.Equal("Delivery mode is disabled.", payload.Delivery.Summary);
    }

    [Fact]
    public async Task Status_ReportsTelegramDirectModeConfiguredWhenLocalSettingsAndSecretArePresent()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using (var settingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            settingsRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 30000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 5,
                DeliveryMode: "Local",
                BackendBaseUrl: null,
                TelegramDeliveryEnabled: true,
                TelegramChatId: 1234567890)));

            using var settingsResponse = await scope.Client.SendAsync(settingsRequest);
            settingsResponse.EnsureSuccessStatusCode();
        }

        using (var secretRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/settings/secrets/telegram"))
        {
            secretRequest.Content = JsonContent.Create(new UpdateTelegramSecretRequestDto("telegram-bot-token"));
            using var secretResponse = await scope.Client.SendAsync(secretRequest);
            secretResponse.EnsureSuccessStatusCode();
        }

        using (var settingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/settings"))
        using (var settingsResponse = await scope.Client.SendAsync(settingsRequest))
        {
            settingsResponse.EnsureSuccessStatusCode();

            var settingsPayload = await settingsResponse.Content.ReadFromJsonAsync<SettingsResponseDto>();
            Assert.NotNull(settingsPayload);
            Assert.Equal("Local", settingsPayload.Settings.DeliveryMode);
            Assert.True(settingsPayload.Settings.TelegramDeliveryEnabled);
            Assert.Equal(1234567890, settingsPayload.Settings.TelegramChatId);
            Assert.True(settingsPayload.Secrets.HasTelegramBotToken);
        }

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status");
        using var response = await scope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<StatusResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("Local", payload.CurrentMode);
        Assert.Equal("configured", payload.Delivery.State);
        Assert.Equal("Telegram local delivery is configured.", payload.Delivery.Summary);
        Assert.Equal("disabled", payload.Backend.State);
        Assert.Equal("Backend health applies only in Cloud mode.", payload.Backend.Summary);
    }

    [Fact]
    public async Task LegacyBindTelegramChatEndpoint_IsNotMapped()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using var actionRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/actions/bind-telegram-chat");
        using var actionResponse = await scope.Client.SendAsync(actionRequest);

        Assert.Equal(HttpStatusCode.NotFound, actionResponse.StatusCode);
    }

    [Fact]
    public async Task StartTelegramChatLink_ReturnsConfirmationCode()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using (var settingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            settingsRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 30000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 5,
                DeliveryMode: "Local",
                BackendBaseUrl: null,
                TelegramDeliveryEnabled: true,
                TelegramChatId: null)));

            using var settingsResponse = await scope.Client.SendAsync(settingsRequest);
            settingsResponse.EnsureSuccessStatusCode();
        }

        using (var secretRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/settings/secrets/telegram"))
        {
            secretRequest.Content = JsonContent.Create(new UpdateTelegramSecretRequestDto("telegram-bot-token"));
            using var secretResponse = await scope.Client.SendAsync(secretRequest);
            secretResponse.EnsureSuccessStatusCode();
        }

        using var actionRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/actions/telegram-link/start");
        using var actionResponse = await scope.Client.SendAsync(actionRequest);
        actionResponse.EnsureSuccessStatusCode();

        var payload = await actionResponse.Content.ReadFromJsonAsync<TelegramChatLinkSessionDto>();
        Assert.NotNull(payload);
        Assert.Equal("pending", payload.Outcome);
        Assert.NotNull(payload.Code);
        Assert.Matches("^[0-9]{6}$", payload.Code!);
        Assert.NotNull(payload.ExpiresAtUtc);
    }

    [Fact]
    public async Task ConfirmTelegramChatLink_BindsChatMatchingPendingCode()
    {
        const string code = "123456";
        var issuedAtUtc = DateTimeOffset.UtcNow;
        var updatesJson = $$"""
            {
              "ok": true,
              "result": [
                {
                  "update_id": 50,
                  "message": {
                    "date": {{issuedAtUtc.ToUnixTimeSeconds()}},
                    "text": "{{code}}",
                    "chat": {
                      "id": 3333333333,
                      "type": "private"
                    }
                  }
                }
              ]
            }
            """;

        await using var scope = await AgentHostScope.CreateAsync(builder =>
        {
            builder.Services.RemoveAll<IHttpClientFactory>();
            builder.Services.AddSingleton<IHttpClientFactory>(
                new StubHttpClientFactory(new StaticHttpMessageHandler(HttpStatusCode.OK, updatesJson)));
        });

        using (var settingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            settingsRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 30000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 5,
                DeliveryMode: "Local",
                BackendBaseUrl: null,
                TelegramDeliveryEnabled: true,
                TelegramChatId: null)));

            using var settingsResponse = await scope.Client.SendAsync(settingsRequest);
            settingsResponse.EnsureSuccessStatusCode();
        }

        using (var secretRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/settings/secrets/telegram"))
        {
            secretRequest.Content = JsonContent.Create(new UpdateTelegramSecretRequestDto("telegram-bot-token"));
            using var secretResponse = await scope.Client.SendAsync(secretRequest);
            secretResponse.EnsureSuccessStatusCode();
        }

        var commands = scope.Host.Services.GetRequiredService<AgentCommandService>();
        var pendingField = typeof(AgentCommandService).GetField("_pendingTelegramChatLink", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pendingField);
        pendingField!.SetValue(commands, new PendingTelegramChatLink(code, issuedAtUtc, issuedAtUtc.AddMinutes(10)));

        using (var actionRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/actions/telegram-link/confirm"))
        using (var actionResponse = await scope.Client.SendAsync(actionRequest))
        {
            actionResponse.EnsureSuccessStatusCode();
            var actionPayload = await actionResponse.Content.ReadFromJsonAsync<ActionResponseDto>();
            Assert.NotNull(actionPayload);
            Assert.Equal("completed", actionPayload.Result.Outcome);
            Assert.Equal("configured", actionPayload.Delivery?.State);
        }

        using var getSettingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/settings");
        using var getSettingsResponse = await scope.Client.SendAsync(getSettingsRequest);
        getSettingsResponse.EnsureSuccessStatusCode();

        var settingsPayload = await getSettingsResponse.Content.ReadFromJsonAsync<SettingsResponseDto>();
        Assert.NotNull(settingsPayload);
        Assert.Equal(3333333333, settingsPayload.Settings.TelegramChatId);
    }

    [Fact]
    public async Task ConfirmTelegramChatLink_IgnoresBacklogMessagesOlderThanPendingCode()
    {
        const string code = "123456";
        var updatesJson = $$"""
            {
              "ok": true,
              "result": [
                {
                  "update_id": 21,
                  "message": {
                    "date": 500,
                    "text": "{{code}}",
                    "chat": {
                      "id": 1111111111,
                      "type": "private"
                    }
                  }
                }
              ]
            }
            """;

        await using var scope = await AgentHostScope.CreateAsync(builder =>
        {
            builder.Services.RemoveAll<IHttpClientFactory>();
            builder.Services.AddSingleton<IHttpClientFactory>(
                new StubHttpClientFactory(new StaticHttpMessageHandler(HttpStatusCode.OK, updatesJson)));
        });

        using (var settingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            settingsRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 30000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 5,
                DeliveryMode: "Local",
                BackendBaseUrl: null,
                TelegramDeliveryEnabled: true,
                TelegramChatId: null)));

            using var settingsResponse = await scope.Client.SendAsync(settingsRequest);
            settingsResponse.EnsureSuccessStatusCode();
        }

        using (var secretRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/settings/secrets/telegram"))
        {
            secretRequest.Content = JsonContent.Create(new UpdateTelegramSecretRequestDto("telegram-bot-token"));
            using var secretResponse = await scope.Client.SendAsync(secretRequest);
            secretResponse.EnsureSuccessStatusCode();
        }

        var commands = scope.Host.Services.GetRequiredService<AgentCommandService>();
        var pendingField = typeof(AgentCommandService).GetField("_pendingTelegramChatLink", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pendingField);
        var issuedAtUtc = DateTimeOffset.UtcNow;
        pendingField!.SetValue(commands, new PendingTelegramChatLink(code, issuedAtUtc, issuedAtUtc.AddMinutes(10)));

        using (var actionRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/actions/telegram-link/confirm"))
        using (var actionResponse = await scope.Client.SendAsync(actionRequest))
        {
            actionResponse.EnsureSuccessStatusCode();
            var actionPayload = await actionResponse.Content.ReadFromJsonAsync<ActionResponseDto>();
            Assert.NotNull(actionPayload);
            Assert.Equal("failed", actionPayload.Result.Outcome);
        }

        using var getSettingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/settings");
        using var getSettingsResponse = await scope.Client.SendAsync(getSettingsRequest);
        getSettingsResponse.EnsureSuccessStatusCode();

        var settingsPayload = await getSettingsResponse.Content.ReadFromJsonAsync<SettingsResponseDto>();
        Assert.NotNull(settingsPayload);
        Assert.Null(settingsPayload.Settings.TelegramChatId);
    }

    [Fact]
    public async Task ConfirmTelegramChatLink_ReturnsTelegramApiErrorWithoutPretendingDeliveryIsMisconfigured()
    {
        const string updatesJson = """
            {
              "ok": false,
              "description": "Bad Request: invalid token"
            }
            """;

        await using var scope = await AgentHostScope.CreateAsync(builder =>
        {
            builder.Services.RemoveAll<IHttpClientFactory>();
            builder.Services.AddSingleton<IHttpClientFactory>(
                new StubHttpClientFactory(new StaticHttpMessageHandler(HttpStatusCode.OK, updatesJson)));
        });

        using (var settingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            settingsRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 30000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 5,
                DeliveryMode: "Local",
                BackendBaseUrl: null,
                TelegramDeliveryEnabled: true,
                TelegramChatId: null)));

            using var settingsResponse = await scope.Client.SendAsync(settingsRequest);
            settingsResponse.EnsureSuccessStatusCode();
        }

        using (var secretRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/settings/secrets/telegram"))
        {
            secretRequest.Content = JsonContent.Create(new UpdateTelegramSecretRequestDto("telegram-bot-token"));
            using var secretResponse = await scope.Client.SendAsync(secretRequest);
            secretResponse.EnsureSuccessStatusCode();
        }

        var commands = scope.Host.Services.GetRequiredService<AgentCommandService>();
        var pendingField = typeof(AgentCommandService).GetField("_pendingTelegramChatLink", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pendingField);
        var issuedAtUtc = DateTimeOffset.UtcNow;
        pendingField!.SetValue(commands, new PendingTelegramChatLink("123456", issuedAtUtc, issuedAtUtc.AddMinutes(10)));

        using var actionRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/actions/telegram-link/confirm");
        using var actionResponse = await scope.Client.SendAsync(actionRequest);
        actionResponse.EnsureSuccessStatusCode();

        var actionPayload = await actionResponse.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.NotNull(actionPayload);
        Assert.Equal("failed", actionPayload.Result.Outcome);
        Assert.Contains("invalid token", actionPayload.Result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual("misconfigured", actionPayload.Delivery?.State);
        Assert.Equal("auth_failed", actionPayload.Delivery?.State);

        using var getSettingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/settings");
        using var getSettingsResponse = await scope.Client.SendAsync(getSettingsRequest);
        getSettingsResponse.EnsureSuccessStatusCode();

        var settingsPayload = await getSettingsResponse.Content.ReadFromJsonAsync<SettingsResponseDto>();
        Assert.NotNull(settingsPayload);
        Assert.Null(settingsPayload.Settings.TelegramChatId);
    }

    [Fact]
    public async Task ConfirmTelegramChatLink_TransportFailureDoesNotExposeBotTokenInResultOrLogs()
    {
        const string botToken = "123456:telegram-secret-token";
        var logs = new CaptureLoggerProvider();

        await using var scope = await AgentHostScope.CreateAsync(builder =>
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(logs);
            builder.Services
                .AddHttpClient(AgentNotificationSender.TelegramHttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new TokenLeakingThrowingHandler(botToken));
        });

        using (var settingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            settingsRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 30000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 5,
                DeliveryMode: "Local",
                BackendBaseUrl: null,
                TelegramDeliveryEnabled: true,
                TelegramChatId: null)));
            using var settingsResponse = await scope.Client.SendAsync(settingsRequest);
            settingsResponse.EnsureSuccessStatusCode();
        }

        using (var secretRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/settings/secrets/telegram"))
        {
            secretRequest.Content = JsonContent.Create(new UpdateTelegramSecretRequestDto(botToken));
            using var secretResponse = await scope.Client.SendAsync(secretRequest);
            secretResponse.EnsureSuccessStatusCode();
        }

        var commands = scope.Host.Services.GetRequiredService<AgentCommandService>();
        var pendingField = typeof(AgentCommandService).GetField("_pendingTelegramChatLink", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(pendingField);
        var issuedAtUtc = DateTimeOffset.UtcNow;
        pendingField!.SetValue(commands, new PendingTelegramChatLink("123456", issuedAtUtc, issuedAtUtc.AddMinutes(10)));

        using var actionRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/actions/telegram-link/confirm");
        using var actionResponse = await scope.Client.SendAsync(actionRequest);
        actionResponse.EnsureSuccessStatusCode();
        var rawBody = await actionResponse.Content.ReadAsStringAsync();
        var actionPayload = JsonSerializer.Deserialize<ActionResponseDto>(rawBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(actionPayload);
        Assert.Equal("failed", actionPayload.Result.Outcome);
        Assert.Equal("Не удалось связаться с Telegram. Повторите попытку.", actionPayload.Result.Message);
        Assert.DoesNotContain(botToken, rawBody, StringComparison.Ordinal);
        Assert.DoesNotContain(botToken, string.Join(Environment.NewLine, logs.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_RecomputesBackendHealthAfterCloudSecretChange()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using (var settingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            settingsRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 30000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 5,
                DeliveryMode: "Cloud",
                BackendBaseUrl: "https://backend.example.test",
                TelegramDeliveryEnabled: false,
                TelegramChatId: null)));

            using var settingsResponse = await scope.Client.SendAsync(settingsRequest);
            settingsResponse.EnsureSuccessStatusCode();
        }

        using (var secretRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/settings/secrets/cloud"))
        {
            secretRequest.Content = JsonContent.Create(new UpdateCloudSecretRequestDto("cloud-auth-key"));
            using var secretResponse = await scope.Client.SendAsync(secretRequest);
            secretResponse.EnsureSuccessStatusCode();
        }

        var controlState = scope.Host.Services.GetRequiredService<AgentControlStateStore>();
        controlState.SetLastBackendHealth(new AgentComponentHealthRecord(
            "healthy",
            "Backend ping succeeded.",
            DateTimeOffset.Parse("2026-07-01T10:30:00+00:00"),
            DateTimeOffset.Parse("2026-07-01T10:30:00+00:00")));

        using (var clearSecretRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/settings/secrets/cloud"))
        {
            clearSecretRequest.Content = JsonContent.Create(new UpdateCloudSecretRequestDto(string.Empty));
            using var clearSecretResponse = await scope.Client.SendAsync(clearSecretRequest);
            clearSecretResponse.EnsureSuccessStatusCode();
        }

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status");
        using var response = await scope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<StatusResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("Cloud", payload.CurrentMode);
        Assert.Equal("not_configured", payload.Backend.State);
        Assert.Equal("Cloud delivery requires a stored auth key.", payload.Backend.Summary);
    }

    [Fact]
    public async Task Status_ReportsCloudBackendMisconfiguredWhenBackendBaseUrlIsInvalid()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using (var settingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            settingsRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 30000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 5,
                DeliveryMode: "Cloud",
                BackendBaseUrl: "ftp://backend.example.test",
                TelegramDeliveryEnabled: false,
                TelegramChatId: null)));

            using var settingsResponse = await scope.Client.SendAsync(settingsRequest);
            settingsResponse.EnsureSuccessStatusCode();
        }

        using (var secretRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/settings/secrets/cloud"))
        {
            secretRequest.Content = JsonContent.Create(new UpdateCloudSecretRequestDto("cloud-auth-key"));
            using var secretResponse = await scope.Client.SendAsync(secretRequest);
            secretResponse.EnsureSuccessStatusCode();
        }

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status");
        using var response = await scope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<StatusResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("Cloud", payload.CurrentMode);
        Assert.Equal("misconfigured", payload.Delivery.State);
        Assert.Equal("BackendBaseUrl must be an absolute http(s) URL.", payload.Delivery.Summary);
        Assert.Equal("misconfigured", payload.Backend.State);
        Assert.Equal("BackendBaseUrl must be an absolute http(s) URL.", payload.Backend.Summary);
    }

    [Fact]
    public async Task Reload_InvalidatesCachedBackendHealthBeforeStatusRead()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using (var settingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            settingsRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 30000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 5,
                DeliveryMode: "Cloud",
                BackendBaseUrl: "https://backend.example.test",
                TelegramDeliveryEnabled: false,
                TelegramChatId: null)));

            using var settingsResponse = await scope.Client.SendAsync(settingsRequest);
            settingsResponse.EnsureSuccessStatusCode();
        }

        using (var secretRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/settings/secrets/cloud"))
        {
            secretRequest.Content = JsonContent.Create(new UpdateCloudSecretRequestDto("cloud-auth-key"));
            using var secretResponse = await scope.Client.SendAsync(secretRequest);
            secretResponse.EnsureSuccessStatusCode();
        }

        var controlState = scope.Host.Services.GetRequiredService<AgentControlStateStore>();
        controlState.SetLastBackendHealth(new AgentComponentHealthRecord(
            "healthy",
            "Backend ping succeeded.",
            DateTimeOffset.Parse("2026-07-01T10:30:00+00:00"),
            DateTimeOffset.Parse("2026-07-01T10:30:00+00:00")));

        var settingsPath = Path.Combine(scope.RootDirectory, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {
              "loopback": {
                "port": 45631
              },
              "monitor": {
                "pollIntervalMs": 30000,
                "idleTimeoutSec": 5,
                "minConfirmLifetimeSec": 5
              },
              "delivery": {
                "mode": "Cloud"
              },
              "telegram": {
                "deliveryEnabled": false,
                "chatId": null
              },
              "cloud": {
                "backendBaseUrl": "ftp://backend.example.test"
              }
            }
            """);

        using (var reloadRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/actions/reload"))
        using (var reloadResponse = await scope.Client.SendAsync(reloadRequest))
        {
            reloadResponse.EnsureSuccessStatusCode();
        }

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status");
        using var response = await scope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<StatusResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal("misconfigured", payload.Backend.State);
        Assert.Equal("BackendBaseUrl must be an absolute http(s) URL.", payload.Backend.Summary);
        Assert.Equal("misconfigured", payload.Delivery.State);
        Assert.Equal("BackendBaseUrl must be an absolute http(s) URL.", payload.Delivery.Summary);
    }

    [Fact]
    public async Task TestNotification_UsesAgentNotificationSender()
    {
        var sender = new StubNotificationSender(new NotificationDispatchResult(
            Delivered: true,
            Health: new DeliveryHealthSnapshot(
                DeliveryMode.Local,
                DeliveryHealthState.Healthy,
                "Telegram delivered to chat_id=123456.",
                DateTimeOffset.Parse("2026-07-01T10:30:00+00:00"),
                DateTimeOffset.Parse("2026-07-01T10:30:00+00:00"))));

        await using var scope = await AgentHostScope.CreateAsync(builder =>
        {
            builder.Services.RemoveAll<IAgentNotificationSender>();
            builder.Services.AddSingleton<IAgentNotificationSender>(sender);
        });

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/actions/test-notification");
        using var response = await scope.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<ActionResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(1, sender.CallCount);
        Assert.Equal("completed", payload.Result.Outcome);
        Assert.Equal("Telegram delivered to chat_id=123456.", payload.Result.Message);
        Assert.NotNull(payload.Delivery);
        Assert.Equal("healthy", payload.Delivery.State);
    }

    [Fact]
    public async Task TestNotification_WithConfiguredTelegramTokenAndBoundChat_IsNotSetupRejected()
    {
        var sender = new StubNotificationSender(new NotificationDispatchResult(
            Delivered: true,
            Health: new DeliveryHealthSnapshot(
                DeliveryMode.Local,
                DeliveryHealthState.Healthy,
                "Telegram delivered to chat_id=123456.",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)));

        await using var scope = await AgentHostScope.CreateAsync(builder =>
        {
            builder.Services.RemoveAll<IAgentNotificationSender>();
            builder.Services.AddSingleton<IAgentNotificationSender>(sender);
        });

        using (var settingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            settingsRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                45631, 30000, 5, 5, "Local", null, true, 123456)));
            using var settingsResponse = await scope.Client.SendAsync(settingsRequest);
            settingsResponse.EnsureSuccessStatusCode();
        }

        using (var secretRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/settings/secrets/telegram"))
        {
            secretRequest.Content = JsonContent.Create(new UpdateTelegramSecretRequestDto("configured-token"));
            using var secretResponse = await scope.Client.SendAsync(secretRequest);
            secretResponse.EnsureSuccessStatusCode();
        }

        using var actionRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/actions/test-notification");
        using var actionResponse = await scope.Client.SendAsync(actionRequest);
        actionResponse.EnsureSuccessStatusCode();
        var payload = await actionResponse.Content.ReadFromJsonAsync<ActionResponseDto>();

        Assert.NotNull(payload);
        Assert.Equal("completed", payload.Result.Outcome);
        Assert.NotEqual("rejected", payload.Result.Outcome);
        Assert.Equal(1, sender.CallCount);
        Assert.NotNull(sender.LastSnapshot);
        Assert.Equal("Local", sender.LastSnapshot.Settings.Delivery.Mode);
        Assert.True(sender.LastSnapshot.Settings.Telegram.DeliveryEnabled);
        Assert.Equal(123456, sender.LastSnapshot.Settings.Telegram.ChatId);
        Assert.Equal("configured-token", sender.LastSnapshot.Secrets.TelegramBotToken);
    }

    [Fact]
    public async Task TestNotification_RejectedDeliveryRecordsDegradedIncident()
    {
        await using var scope = await AgentHostScope.CreateAsync();

        using (var settingsRequest = scope.CreateAuthorizedRequest(HttpMethod.Put, "/v1/settings"))
        {
            settingsRequest.Content = JsonContent.Create(new UpdateSettingsRequestDto(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 30000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 5,
                DeliveryMode: "Local",
                BackendBaseUrl: null,
                TelegramDeliveryEnabled: true,
                TelegramChatId: 1234567890)));

            using var settingsResponse = await scope.Client.SendAsync(settingsRequest);
            settingsResponse.EnsureSuccessStatusCode();
        }

        using (var actionRequest = scope.CreateAuthorizedRequest(HttpMethod.Post, "/v1/actions/test-notification"))
        using (var actionResponse = await scope.Client.SendAsync(actionRequest))
        {
            actionResponse.EnsureSuccessStatusCode();
            var actionPayload = await actionResponse.Content.ReadFromJsonAsync<ActionResponseDto>();
            Assert.NotNull(actionPayload);
            Assert.Equal("rejected", actionPayload.Result.Outcome);
            Assert.Equal("not_configured", actionPayload.Delivery?.State);
        }

        using var incidentsRequest = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/incidents?limit=5");
        using var incidentsResponse = await scope.Client.SendAsync(incidentsRequest);
        incidentsResponse.EnsureSuccessStatusCode();

        var incidents = await incidentsResponse.Content.ReadFromJsonAsync<IncidentsResponseDto>();
        Assert.NotNull(incidents);
        Assert.Contains(incidents.Items, item => item.Kind == "delivery_degraded" && item.Severity == "warning");
    }

    [Fact]
    public async Task LoopbackBinding_UsesRealKestrelLoopbackEndpoint()
    {
        const string settingsJson = """
            {
              "loopback": {
                "port": 0
              }
            }
            """;

        await using var scope = await AgentHostScope.CreateAsync(
            useTestServer: false,
            initialSettingsJson: settingsJson);

        Assert.True(IPAddress.TryParse(scope.BaseAddress.Host, out var hostAddress));
        Assert.True(IPAddress.IsLoopback(hostAddress));

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status");
        using var response = await scope.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<StatusResponseDto>();
        Assert.NotNull(payload);
        Assert.Equal(scope.BaseAddress.Port, payload.ActiveLoopbackPort);
    }

    [Fact]
    public async Task LocalApiToken_PersistsAndStillGatesRequestsAcrossHostRestart()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
        string firstToken;

        await using (var firstScope = await AgentHostScope.CreateAsync(
            rootDirectory: rootDirectory,
            deleteRootDirectoryOnDispose: false))
        {
            firstToken = firstScope.Token;
            Assert.False(string.IsNullOrWhiteSpace(firstToken));
        }

        await using var restartedScope = await AgentHostScope.CreateAsync(rootDirectory: rootDirectory);
        Assert.Equal(firstToken, restartedScope.Token);

        using (var request = restartedScope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status", firstToken))
        using (var response = await restartedScope.Client.SendAsync(request))
        {
            response.EnsureSuccessStatusCode();
        }

        using (var request = restartedScope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status", "not-the-token"))
        using (var response = await restartedScope.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task RecoveryMode_AllowsUnauthenticatedDashboardReadsAndSecretRewriteOnly()
    {
        var unreadablePayload = new byte[] { 0x01, 0x02, 0x03, 0x04 };

        await using var scope = await AgentHostScope.CreateAsync(initialSecretsBytes: unreadablePayload);
        var secretsPath = Path.Combine(scope.RootDirectory, "secrets.dat");

        using (var statusResponse = await scope.Client.GetAsync("/v1/status"))
        {
            statusResponse.EnsureSuccessStatusCode();
        }

        using (var settingsResponse = await scope.Client.GetAsync("/v1/settings"))
        {
            settingsResponse.EnsureSuccessStatusCode();
            var payload = await settingsResponse.Content.ReadFromJsonAsync<SettingsResponseDto>();
            Assert.NotNull(payload);
            Assert.True(payload.Secrets.HasLocalApiToken);
        }

        using (var incidentsResponse = await scope.Client.GetAsync("/v1/incidents?limit=5"))
        {
            incidentsResponse.EnsureSuccessStatusCode();
        }

        using (var blockedResponse = await scope.Client.PutAsJsonAsync(
                   "/v1/settings",
                   new UpdateSettingsRequestDto(new LocalControlSettingsDto(45631, 30000, 5, 5, "Disabled", null, false, null))))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, blockedResponse.StatusCode);
        }

        Assert.Equal(unreadablePayload, File.ReadAllBytes(secretsPath));

        using (var secretResponse = await scope.Client.PostAsJsonAsync(
                   "/v1/settings/secrets/telegram",
                   new UpdateTelegramSecretRequestDto("telegram-bot-token")))
        {
            secretResponse.EnsureSuccessStatusCode();
        }

        var persistedToken = scope.Host.Services.GetRequiredService<AgentConfigurationService>().GetSnapshot().Secrets.LocalApiToken;
        Assert.False(string.IsNullOrWhiteSpace(persistedToken));
        Assert.NotEqual(unreadablePayload, File.ReadAllBytes(secretsPath));

        using (var unauthorizedAfterRecovery = await scope.Client.GetAsync("/v1/status"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorizedAfterRecovery.StatusCode);
        }

        using (var authorizedAfterRecovery = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/status", persistedToken))
        using (var authorizedResponse = await scope.Client.SendAsync(authorizedAfterRecovery))
        {
            authorizedResponse.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Startup_RecordsRecoveryIncidentWhenPreviousRunWasNotCleanlyStopped()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);

        var markerPath = Path.Combine(rootDirectory, "runtime-state.json");
        var marker = new AgentLifecycleMarker(
            AgentRuntimeStatus.Running,
            DateTimeOffset.Parse("2026-07-03T07:30:00+00:00"));
        await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(marker, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        await using var scope = await AgentHostScope.CreateAsync(rootDirectory: rootDirectory);

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/incidents?limit=5");
        using var response = await scope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<IncidentsResponseDto>();
        Assert.NotNull(payload);
        Assert.Contains(payload.Items, item => item.Kind == "agent_recovered_after_unclean_shutdown" && item.Severity == "warning");
    }

    [Fact]
    public async Task Startup_RecordsRecoveryIncidentWhenPreviousRunFaulted()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);

        var markerPath = Path.Combine(rootDirectory, "runtime-state.json");
        var marker = new AgentLifecycleMarker(
            AgentRuntimeStatus.Faulted,
            DateTimeOffset.Parse("2026-07-03T07:45:00+00:00"));
        await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(marker, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        await using var scope = await AgentHostScope.CreateAsync(rootDirectory: rootDirectory);

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/incidents?limit=5");
        using var response = await scope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<IncidentsResponseDto>();
        Assert.NotNull(payload);
        Assert.Contains(payload.Items, item => item.Kind == "agent_recovered_after_fault" && item.Severity == "warning");
    }

    [Fact]
    public async Task Startup_RecordsRecoveryIncidentWhenPreviousMarkerIsUnreadable()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);

        var markerPath = Path.Combine(rootDirectory, "runtime-state.json");
        await File.WriteAllTextAsync(markerPath, "{\"status\":\"Running\"");

        await using var scope = await AgentHostScope.CreateAsync(rootDirectory: rootDirectory);

        using var request = scope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/incidents?limit=5");
        using var response = await scope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<IncidentsResponseDto>();
        Assert.NotNull(payload);
        Assert.Contains(payload.Items, item =>
            item.Kind == "agent_recovered_with_unreadable_runtime_state" &&
            item.Severity == "warning" &&
            item.Summary.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Startup_DoesNotRecordRecoveryIncidentAfterCleanStop()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));

        await using (var firstScope = await AgentHostScope.CreateAsync(
            rootDirectory: rootDirectory,
            deleteRootDirectoryOnDispose: false))
        {
        }

        await using var restartedScope = await AgentHostScope.CreateAsync(rootDirectory: rootDirectory);

        using var request = restartedScope.CreateAuthorizedRequest(HttpMethod.Get, "/v1/incidents?limit=5");
        using var response = await restartedScope.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<IncidentsResponseDto>();
        Assert.NotNull(payload);
        Assert.DoesNotContain(payload.Items, item => item.Kind == "agent_recovered_after_unclean_shutdown");
        Assert.DoesNotContain(payload.Items, item => item.Kind == "agent_recovered_after_fault");
    }

    [Fact]
    public async Task ProbeWriteFailure_DoesNotStopRuntimeOrPreventStatusUpdates()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");
        Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", rootDirectory);

        try
        {
            var markerPath = Path.Combine(rootDirectory, "runtime-state.json");
            Directory.CreateDirectory(markerPath);

            var stateStore = new AgentRuntimeStateStore();
            var service = CreateRuntimeService(
                stateStore,
                new StubRuntimeProbe(new AgentRuntimeProbeSnapshot(
                    DateTimeOffset.Parse("2026-07-03T08:00:01+00:00"),
                    CandidateProcessCount: 1,
                    GameConnectionCount: 0,
                    Connections: [])));

            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);

            var snapshot = stateStore.GetSnapshot();
            Assert.Equal(AgentRuntimeStatus.Running, snapshot.Status);
            Assert.Equal(1, snapshot.ProbeCount);
            Assert.Equal(1, snapshot.LastObservedProcessCount);
            Assert.Null(snapshot.LastError);
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task FaultedProbe_StillRecordsAgentFaultedIncidentWhenMarkerWriteFails()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");
        Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", rootDirectory);

        try
        {
            var markerPath = Path.Combine(rootDirectory, "runtime-state.json");
            Directory.CreateDirectory(markerPath);

            var stateStore = new AgentRuntimeStateStore();
            var controlState = new AgentControlStateStore();
            var service = CreateRuntimeService(
                stateStore,
                new ThrowingRuntimeProbe(new InvalidOperationException("probe exploded")),
                controlState);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                InvokeNonPublicTaskAsync(service, "ExecuteAsync", CancellationToken.None));

            Assert.Equal("probe exploded", exception.Message);
            Assert.Equal(AgentRuntimeStatus.Faulted, stateStore.GetSnapshot().Status);
            Assert.Equal("probe exploded", stateStore.GetSnapshot().LastError);
            Assert.Contains(controlState.GetIncidents(10), item => item.Kind == "agent_faulted" && item.Severity == "error");
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProbeAsync_DoesNotEmitDisconnectForUnconfirmedConnection()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");
        Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", rootDirectory);

        try
        {
            WriteSettings(rootDirectory, minConfirmLifetimeSec: 15);

            var stateStore = new AgentRuntimeStateStore();
            var controlState = new AgentControlStateStore();
            var sender = new RecordingNotificationSender();
            var service = CreateRuntimeService(
                stateStore,
                new SequenceRuntimeProbe(
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-07-07T10:00:00+00:00"),
                        CandidateProcessCount: 1,
                        GameConnectionCount: 1,
                        Connections:
                        [
                            new AgentRuntimeConnectionSnapshot("pid-999999", "l2.bin", "Lineage II", "Connected", DateTimeOffset.Parse("2026-07-07T10:00:00+00:00")),
                        ]),
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-07-07T10:00:10+00:00"),
                        CandidateProcessCount: 0,
                        GameConnectionCount: 0,
                        Connections: [])),
                controlState,
                sender);

            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);
            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);

            Assert.Equal(0, sender.CallCount);
            Assert.DoesNotContain(controlState.GetIncidents(10), item => item.Kind is "client_disconnected" or "process_exited");
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProbeAsync_DoesNotEmitClosureForNeverActiveConnectingProcess()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");
        Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", rootDirectory);

        try
        {
            WriteSettings(rootDirectory, minConfirmLifetimeSec: 15);

            var stateStore = new AgentRuntimeStateStore();
            var controlState = new AgentControlStateStore();
            var sender = new RecordingNotificationSender();
            var service = CreateRuntimeService(
                stateStore,
                new SequenceRuntimeProbe(
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-07-07T10:00:00+00:00"),
                        CandidateProcessCount: 1,
                        GameConnectionCount: 0,
                        Connections:
                        [
                            new AgentRuntimeConnectionSnapshot("pid-727272", "l2.bin", "Lineage II", "Connecting", DateTimeOffset.Parse("2026-07-07T10:00:00+00:00")),
                        ]),
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-07-07T10:00:20+00:00"),
                        CandidateProcessCount: 1,
                        GameConnectionCount: 0,
                        Connections:
                        [
                            new AgentRuntimeConnectionSnapshot("pid-727272", "l2.bin", "Lineage II", "Connecting", DateTimeOffset.Parse("2026-07-07T10:00:20+00:00")),
                        ]),
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-07-07T10:00:40+00:00"),
                        CandidateProcessCount: 0,
                        GameConnectionCount: 0,
                        Connections: [])),
                controlState,
                sender);

            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);
            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);
            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);

            Assert.Equal(0, sender.CallCount);
            Assert.Empty(sender.MonitorEvents);
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProbeAsync_EmitsDisconnectRecoveryAndClosureForOneClient()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");
        Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", rootDirectory);

        try
        {
            WriteSettings(rootDirectory, minConfirmLifetimeSec: 15);
            var sender = new RecordingNotificationSender();
            var service = CreateRuntimeService(
                new AgentRuntimeStateStore(),
                new SequenceRuntimeProbe(
                    Snapshot("2026-10-04T10:00:00+00:00", "Connected"),
                    Snapshot("2026-10-04T10:00:30+00:00", "Connecting"),
                    Snapshot("2026-10-04T10:01:00+00:00", "Connected"),
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-10-04T10:01:30+00:00"),
                        CandidateProcessCount: 0,
                        GameConnectionCount: 0,
                        Connections: [])),
                notificationSender: sender);

            for (var index = 0; index < 4; index++)
            {
                await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);
            }

            Assert.Equal(
                [MonitorEventKind.ClientDisconnected, MonitorEventKind.IdleBack, MonitorEventKind.ProcessExited],
                sender.MonitorEvents.Select(static item => item.Kind).ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }

        static AgentRuntimeProbeSnapshot Snapshot(string timestamp, string state) =>
            new(
                DateTimeOffset.Parse(timestamp),
                CandidateProcessCount: 1,
                GameConnectionCount: state == "Connected" ? 1 : 0,
                Connections:
                [
                    new AgentRuntimeConnectionSnapshot(
                        "pid-424242", "l2.bin", "Lineage II", state, DateTimeOffset.Parse(timestamp)),
                ]);
    }

    [Fact]
    public async Task ProbeAsync_EmitsProcessExitEvenWhenLastStateWasConnecting()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");
        Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", rootDirectory);

        try
        {
            WriteSettings(rootDirectory, minConfirmLifetimeSec: 15);
            var sender = new RecordingNotificationSender();
            var service = CreateRuntimeService(
                new AgentRuntimeStateStore(),
                new SequenceRuntimeProbe(
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-10-04T09:59:00+00:00"), 1, 1,
                        [new AgentRuntimeConnectionSnapshot("pid-999999", "l2.bin", "Lineage II", "Connected", DateTimeOffset.Parse("2026-10-04T09:59:00+00:00"))]),
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-10-04T10:00:00+00:00"), 1, 0,
                        [new AgentRuntimeConnectionSnapshot("pid-999999", "l2.bin", "Lineage II", "Connecting", DateTimeOffset.Parse("2026-10-04T10:00:00+00:00"))]),
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-10-04T10:00:05+00:00"), 0, 0, [])),
                notificationSender: sender);

            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);
            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);
            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);

            Assert.Equal(
                [MonitorEventKind.ClientDisconnected, MonitorEventKind.ProcessExited],
                sender.MonitorEvents.Select(static item => item.Kind).ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProbeAsync_GameplayOnlyStateDoesNotEmitGhostDisconnectIncident()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");
        Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", rootDirectory);

        try
        {
            WriteSettings(rootDirectory, minConfirmLifetimeSec: 15);

            var stateStore = new AgentRuntimeStateStore();
            var controlState = new AgentControlStateStore();
            var sender = new RecordingNotificationSender();
            var service = CreateRuntimeService(
                stateStore,
                new SequenceRuntimeProbe(
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-07-07T10:00:00+00:00"),
                        CandidateProcessCount: 1,
                        GameConnectionCount: 1,
                        Connections:
                        [
                            new AgentRuntimeConnectionSnapshot("pid-424242", "l2.bin", "Lineage II", "Connected", DateTimeOffset.Parse("2026-07-07T10:00:00+00:00")),
                        ]),
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-07-07T10:00:10+00:00"),
                        CandidateProcessCount: 1,
                        GameConnectionCount: 1,
                        Connections:
                        [
                            new AgentRuntimeConnectionSnapshot(
                                "pid-424242",
                                "l2.bin",
                                "Lineage II",
                                "GameplayOnly",
                                DateTimeOffset.Parse("2026-07-07T10:00:10+00:00"),
                                new AgentConnectionForensics(
                                    "gameplay_only",
                                    "Gameplay socket observed without session socket.",
                                    1,
                                    [7777])),
                        ]),
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-07-07T10:00:26+00:00"),
                        CandidateProcessCount: 1,
                        GameConnectionCount: 1,
                        Connections:
                        [
                            new AgentRuntimeConnectionSnapshot(
                                "pid-424242",
                                "l2.bin",
                                "Lineage II",
                                "GameplayOnly",
                                DateTimeOffset.Parse("2026-07-07T10:00:26+00:00"),
                                new AgentConnectionForensics(
                                    "gameplay_only",
                                    "Gameplay socket observed without session socket.",
                                    1,
                                    [7777])),
                        ])),
                controlState,
                sender);

            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);
            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);
            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);

            Assert.Equal(0, sender.CallCount);
            Assert.DoesNotContain(controlState.GetIncidents(10), item => item.Kind == "ghost_disconnect_suspected");
            var snapshot = stateStore.GetSnapshot();
            Assert.Equal("GameplayOnly", snapshot.Connections.Single().State);
            Assert.NotNull(snapshot.Connections.Single().Forensics);
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProbeAsync_SuppressesClosureForUnprovenLegacyGhostState()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");
        Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", rootDirectory);

        try
        {
            WriteSettings(rootDirectory, minConfirmLifetimeSec: 15);

            var stateStore = new AgentRuntimeStateStore();
            stateStore.Update(new AgentRuntimeState(
                AgentRuntimeStatus.Running,
                DateTimeOffset.Parse("2026-07-07T09:59:00+00:00"),
                DateTimeOffset.Parse("2026-07-07T10:00:20+00:00"),
                null,
                ProbeCount: 1,
                LastObservedProcessCount: 1,
                LastObservedConnectionCount: 0,
                Connections:
                [
                    new AgentConnectionRecord("pid-515151", "l2.bin", "Lineage II", "SuspectedGhostDisconnect", DateTimeOffset.Parse("2026-07-07T10:00:20+00:00")),
                ]));
            var controlState = new AgentControlStateStore();
            var sender = new RecordingNotificationSender();
            var service = CreateRuntimeService(
                stateStore,
                new SequenceRuntimeProbe(
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-07-07T10:00:40+00:00"),
                        CandidateProcessCount: 0,
                        GameConnectionCount: 0,
                        Connections: [])),
                controlState,
                sender);

            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);

            Assert.Equal(0, sender.CallCount);
            Assert.Empty(sender.MonitorEvents);
            Assert.DoesNotContain(controlState.GetIncidents(10), item => item.Kind is "ghost_disconnect_suspected" or "client_disconnected");
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProbeAsync_IgnoredWindowDoesNotFabricateDisconnectForLiveClient()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");
        Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", rootDirectory);

        try
        {
            WriteSettings(rootDirectory, minConfirmLifetimeSec: 0, ignoredWindowTitlesText: "Updater");

            var stateStore = new AgentRuntimeStateStore();
            var controlState = new AgentControlStateStore();
            var sender = new RecordingNotificationSender();
            var service = CreateRuntimeService(
                stateStore,
                new SequenceRuntimeProbe(
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-07-07T10:00:00+00:00"),
                        CandidateProcessCount: 1,
                        GameConnectionCount: 1,
                        Connections:
                        [
                            new AgentRuntimeConnectionSnapshot("pid-626262", "l2.bin", "Updater", "Connected", DateTimeOffset.Parse("2026-07-07T10:00:00+00:00")),
                        ]),
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-07-07T10:00:05+00:00"),
                        CandidateProcessCount: 0,
                        GameConnectionCount: 0,
                        Connections: [])),
                controlState,
                sender);

            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);
            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);

            Assert.Equal(0, sender.CallCount);
            Assert.DoesNotContain(controlState.GetIncidents(10), item => item.Kind is "client_disconnected" or "process_exited");
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ProbeAsync_CollapsesRepeatedDeathAudioMatchesIntoSingleNotification()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootDirectory);
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");
        Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", rootDirectory);

        try
        {
            WriteSettings(rootDirectory, minConfirmLifetimeSec: 15);

            var stateStore = new AgentRuntimeStateStore();
            var controlState = new AgentControlStateStore();
            var sender = new RecordingNotificationSender();
            var audioDetector = new SequenceAudioDeathDetector(
                [
                    [
                        new AudioDeathDetection(2924, DateTimeOffset.Parse("2026-07-09T08:06:45+00:00"), 0.21, "audio_trimmed.m4a"),
                        new AudioDeathDetection(2924, DateTimeOffset.Parse("2026-07-09T08:06:47+00:00"), 0.42, "audio_trimmed.m4a"),
                        new AudioDeathDetection(2924, DateTimeOffset.Parse("2026-07-09T08:06:49+00:00"), 0.31, "audio_trimmed.m4a"),
                    ],
                    [
                        new AudioDeathDetection(2924, DateTimeOffset.Parse("2026-07-09T08:07:15+00:00"), 0.55, "audio_trimmed.m4a"),
                    ],
                ]);
            var service = CreateRuntimeService(
                stateStore,
                new SequenceRuntimeProbe(
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-07-09T08:07:00+00:00"),
                        CandidateProcessCount: 1,
                        GameConnectionCount: 1,
                        Connections:
                        [
                            new AgentRuntimeConnectionSnapshot("pid-2924", "L2", "Lineage II", "Connected", DateTimeOffset.Parse("2026-07-09T08:07:00+00:00")),
                        ]),
                    new AgentRuntimeProbeSnapshot(
                        DateTimeOffset.Parse("2026-07-09T08:07:30+00:00"),
                        CandidateProcessCount: 1,
                        GameConnectionCount: 1,
                        Connections:
                        [
                            new AgentRuntimeConnectionSnapshot("pid-2924", "L2", "Lineage II", "Connected", DateTimeOffset.Parse("2026-07-09T08:07:30+00:00")),
                        ])),
                controlState,
                sender,
                audioDetector);

            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);
            await InvokeNonPublicTaskAsync(service, "ProbeAsync", CancellationToken.None);

            Assert.Equal(1, sender.CallCount);
            Assert.Single(sender.MonitorEvents);
            Assert.Equal(MonitorEventKind.DeadStarted, sender.MonitorEvents[0].Kind);
            Assert.Contains(controlState.GetIncidents(10), item => item.Kind == "dead_started");
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            if (Directory.Exists(rootDirectory))
            {
                Directory.Delete(rootDirectory, recursive: true);
            }
        }
    }

    private sealed class AgentHostScope : IAsyncDisposable
    {
        private readonly string? _previousAgentHome;
        private readonly bool _deleteRootDirectoryOnDispose;

        private AgentHostScope(
            string rootDirectory,
            string? previousAgentHome,
            bool deleteRootDirectoryOnDispose,
            IHost host,
            HttpClient client,
            string token,
            Uri baseAddress)
        {
            RootDirectory = rootDirectory;
            _previousAgentHome = previousAgentHome;
            _deleteRootDirectoryOnDispose = deleteRootDirectoryOnDispose;
            Host = host;
            Client = client;
            Token = token;
            BaseAddress = baseAddress;
        }

        public string RootDirectory { get; }
        public IHost Host { get; }
        public HttpClient Client { get; }
        public string Token { get; }
        public Uri BaseAddress { get; }

        public static async Task<AgentHostScope> CreateAsync(
            Action<WebApplicationBuilder>? extraBuilderSetup = null,
            bool useTestServer = true,
            string? rootDirectory = null,
            bool deleteRootDirectoryOnDispose = true,
            string? initialSettingsJson = null,
            byte[]? initialSecretsBytes = null)
        {
            rootDirectory ??= Path.Combine(Path.GetTempPath(), "l2monitor-agent-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootDirectory);

            if (initialSettingsJson is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(rootDirectory, "settings.json"), initialSettingsJson);
            }

            if (initialSecretsBytes is not null)
            {
                await File.WriteAllBytesAsync(Path.Combine(rootDirectory, "secrets.dat"), initialSecretsBytes);
            }

            var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", rootDirectory);

            var host = AgentHostBuilder.Build(
                [],
                builder =>
                {
                    if (useTestServer)
                    {
                        builder.WebHost.UseTestServer();
                    }

                    extraBuilderSetup?.Invoke(builder);
                },
                loopbackPortOverride: useTestServer ? null : 0);

            await host.StartAsync();

            HttpClient client;
            Uri baseAddress;
            if (useTestServer)
            {
                client = host.GetTestClient();
                baseAddress = client.BaseAddress ?? new Uri("http://localhost");
            }
            else
            {
                var server = host.Services.GetRequiredService<IServer>();
                var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
                var address = addresses?.SingleOrDefault()
                    ?? throw new InvalidOperationException("Expected Kestrel to publish exactly one loopback address.");
                baseAddress = new Uri(address, UriKind.Absolute);
                client = new HttpClient
                {
                    BaseAddress = baseAddress,
                };
            }

            var token = host.Services.GetRequiredService<AgentConfigurationService>().GetSnapshot().Secrets.LocalApiToken;
            return new AgentHostScope(rootDirectory, previousAgentHome, deleteRootDirectoryOnDispose, host, client, token, baseAddress);
        }

        public HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string relativePath, string? tokenOverride = null)
        {
            var request = new HttpRequestMessage(method, relativePath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenOverride ?? Token);
            return request;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Host.StopAsync();
            }
            finally
            {
                Host.Dispose();
                Client.Dispose();
                Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", _previousAgentHome);

                if (_deleteRootDirectoryOnDispose && Directory.Exists(RootDirectory))
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }
            }
        }
    }

    private sealed class StubRuntimeProbe(AgentRuntimeProbeSnapshot snapshot) : IAgentRuntimeProbe
    {
        private readonly AgentRuntimeProbeSnapshot _snapshot = snapshot;

        public Task<AgentRuntimeProbeSnapshot> ProbeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshot);
        }
    }

    private sealed class SequenceRuntimeProbe(params AgentRuntimeProbeSnapshot[] snapshots) : IAgentRuntimeProbe
    {
        private readonly Queue<AgentRuntimeProbeSnapshot> _snapshots = new(snapshots);

        public Task<AgentRuntimeProbeSnapshot> ProbeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_snapshots.Dequeue());
        }
    }

    private sealed class ThrowingRuntimeProbe(Exception exception) : IAgentRuntimeProbe
    {
        private readonly Exception _exception = exception;

        public Task<AgentRuntimeProbeSnapshot> ProbeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<AgentRuntimeProbeSnapshot>(_exception);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubReleaseUpdateChecker : IAgentReleaseUpdateChecker
    {
        public Task<AgentUpdateCheckResult> CheckAsync(
            Uri backendBaseUri,
            string currentVersion,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AgentUpdateCheckResult(
                "available",
                currentVersion,
                "9.9.9",
                true,
                false,
                "https://github.com/hellsmenser/l2monitor/releases/tag/v9.9.9",
                DateTimeOffset.UtcNow));
    }

    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        private readonly Exception _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<HttpResponseMessage>(_exception);
        }
    }

    private sealed class TokenLeakingThrowingHandler(string botToken) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException($"Request {request.RequestUri} failed; token={botToken}"));
    }

    private sealed class CaptureLoggerProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public List<string> Messages { get; } = [];

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CaptureLogger(List<string> messages) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(
                Microsoft.Extensions.Logging.LogLevel logLevel,
                Microsoft.Extensions.Logging.EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                messages.Add(formatter(state, exception));
                if (exception is not null)
                {
                    messages.Add(exception.ToString());
                }
            }
        }
    }

    private sealed class StaticHttpMessageHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode = statusCode;
        private readonly string _content = content;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StubHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => _stopping.Cancel();
    }

    private sealed class StubRestartCoordinator(AgentRestartDecision decision) : IAgentRestartCoordinator
    {
        private readonly AgentRestartDecision _decision = decision;

        public AgentRestartDecision ScheduleRestart() => _decision;
    }

    private sealed class StubNotificationSender(NotificationDispatchResult result) : IAgentNotificationSender
    {
        private readonly NotificationDispatchResult _result = result;

        public int CallCount { get; private set; }
        public AgentConfigurationSnapshot? LastSnapshot { get; private set; }

        public Task<NotificationDispatchResult> SendTestNotificationAsync(
            AgentConfigurationSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastSnapshot = snapshot;
            return Task.FromResult(_result);
        }

        public Task<NotificationDispatchResult> SendMonitorEventAsync(
            AgentConfigurationSnapshot snapshot,
            MonitorEvent monitorEvent,
            IReadOnlyDictionary<string, string?>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(_result);
        }
    }

    private sealed class StubAudioDeathDetector : IAudioDeathDetector
    {
        public void UpdateTrackedProcessIds(IReadOnlyList<int> processIds)
        {
        }

        public IReadOnlyList<AudioDeathDetection> DrainDetections(DateTimeOffset observedAtUtc) => [];
    }

    private sealed class SequenceAudioDeathDetector(IReadOnlyList<IReadOnlyList<AudioDeathDetection>> batches) : IAudioDeathDetector
    {
        private readonly Queue<IReadOnlyList<AudioDeathDetection>> _batches = new(batches);

        public void UpdateTrackedProcessIds(IReadOnlyList<int> processIds)
        {
        }

        public IReadOnlyList<AudioDeathDetection> DrainDetections(DateTimeOffset observedAtUtc)
            => _batches.Count == 0 ? [] : _batches.Dequeue();
    }

    private sealed class RecordingNotificationSender() : IAgentNotificationSender
    {
        private static readonly NotificationDispatchResult DispatchResult =
            new(true, new DeliveryHealthSnapshot(DeliveryMode.Disabled, DeliveryHealthState.Disabled, "disabled", DateTimeOffset.UtcNow));

        public int CallCount { get; private set; }
        public List<MonitorEvent> MonitorEvents { get; } = [];

        public Task<NotificationDispatchResult> SendTestNotificationAsync(
            AgentConfigurationSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(DispatchResult);
        }

        public Task<NotificationDispatchResult> SendMonitorEventAsync(
            AgentConfigurationSnapshot snapshot,
            MonitorEvent monitorEvent,
            IReadOnlyDictionary<string, string?>? metadata = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            MonitorEvents.Add(monitorEvent);
            return Task.FromResult(DispatchResult);
        }
    }

    private static AgentRuntimeService CreateRuntimeService(
        AgentRuntimeStateStore stateStore,
        IAgentRuntimeProbe probe,
        AgentControlStateStore? controlState = null,
        IAgentNotificationSender? notificationSender = null,
        IAudioDeathDetector? audioDeathDetector = null)
    {
        controlState ??= new AgentControlStateStore();
        return new AgentRuntimeService(
            new AgentLaunchOptions(RunOnce: false),
            TimeProvider.System,
            stateStore,
            new AgentLifecycleMarkerStore(),
            controlState,
            new AgentConfigurationService(),
            probe,
            audioDeathDetector ?? new StubAudioDeathDetector(),
            notificationSender ?? new StubNotificationSender(new NotificationDispatchResult(true, new DeliveryHealthSnapshot(DeliveryMode.Disabled, DeliveryHealthState.Disabled, "disabled", DateTimeOffset.UtcNow))),
            new StubHostApplicationLifetime(),
            AgentRuntimeOptions.Default,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRuntimeService>.Instance);
    }

    private static void WriteSettings(string rootDirectory, int minConfirmLifetimeSec, string? ignoredWindowTitlesText = null)
    {
        var settings = AgentSettings.Default with
        {
            Monitor = AgentSettings.Default.Monitor with
            {
                MinConfirmLifetimeSec = minConfirmLifetimeSec,
                IgnoredWindowTitlesText = ignoredWindowTitlesText ?? string.Empty,
            },
        };

        File.WriteAllText(
            Path.Combine(rootDirectory, "settings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            }));
    }

    private static async Task InvokeNonPublicTaskAsync(object instance, string methodName, CancellationToken cancellationToken)
    {
        var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(instance, [cancellationToken]) as Task;
        Assert.NotNull(task);
        await task!;
    }
}

