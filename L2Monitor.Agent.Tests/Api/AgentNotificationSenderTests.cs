using System.Net;
using System.Text;
using L2Monitor.Agent.Hosting;
using L2Monitor.Core.Api;
using L2Monitor.Core.Delivery;
using L2Monitor.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace L2Monitor.Agent.Tests.Api;

public sealed class AgentNotificationSenderTests
{
    [Fact]
    public void BuildMonitorEventText_SelectsSingleTrimmedVariantAndRendersPlaceholders()
    {
        var snapshot = CreateSnapshot(
            ghostDisconnectMessageTemplate: " first {pid} \r\n\r\n second {name} ");
        var monitorEvent = new MonitorEvent(
            new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc),
            4242,
            "Lineage II",
            MonitorEventKind.GhostDisconnectSuspected,
            0);

        var first = AgentNotificationSender.BuildMonitorEventText(snapshot, monitorEvent, "window", variantCount =>
        {
            Assert.Equal(2, variantCount);
            return 0;
        });
        var second = AgentNotificationSender.BuildMonitorEventText(snapshot, monitorEvent, "window", variantCount =>
        {
            Assert.Equal(2, variantCount);
            return 1;
        });

        Assert.Equal("first 4242", first);
        Assert.Equal("second Lineage II", second);
    }

    [Fact]
    public async Task SendMonitorEventAsync_TelegramPostsOnlyOneVariantFromMultilineTemplate()
    {
        var handler = new CaptureHandler();
        var sender = new AgentNotificationSender(
            new StubHttpClientFactory(handler),
            TimeProvider.System,
            NullLogger<AgentNotificationSender>.Instance);
        var snapshot = CreateSnapshot(
            ghostDisconnectMessageTemplate: "first {pid}\r\nsecond {name}");
        var monitorEvent = new MonitorEvent(
            new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc),
            4242,
            "Lineage II",
            MonitorEventKind.GhostDisconnectSuspected,
            0);

        var result = await sender.SendMonitorEventAsync(snapshot, monitorEvent);

        Assert.True(result.Delivered);
        Assert.NotNull(handler.LastFormBody);
        Assert.Contains("chat_id=123", handler.LastFormBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("%0D%0A", handler.LastFormBody!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%0A", handler.LastFormBody!, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            handler.LastFormBody!.Contains("text=first+4242", StringComparison.Ordinal)
            || handler.LastFormBody.Contains("text=second+Lineage+II", StringComparison.Ordinal),
            $"Expected a single rendered variant, got: {handler.LastFormBody}");
    }

    [Fact]
    public async Task SendMonitorEventAsync_SuppressesDisabledNotificationTypes()
    {
        var handler = new CaptureHandler();
        var sender = new AgentNotificationSender(
            new StubHttpClientFactory(handler),
            TimeProvider.System,
            NullLogger<AgentNotificationSender>.Instance);
        var snapshot = CreateSnapshot(
            ghostDisconnectMessageTemplate: "first {pid}",
            disconnectNotificationEnabled: false);
        var monitorEvent = new MonitorEvent(
            new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc),
            4242,
            "Lineage II",
            MonitorEventKind.GhostDisconnectSuspected,
            0);

        var result = await sender.SendMonitorEventAsync(snapshot, monitorEvent);

        Assert.False(result.Delivered);
        Assert.True(result.Suppressed);
        Assert.Null(handler.LastFormBody);
        Assert.Contains("disabled by settings", result.Health.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendMonitorEventAsync_SuppressesDisabledNotificationTypes_ForCloudDispatch()
    {
        var handler = new CaptureHandler();
        var sender = new AgentNotificationSender(
            new StubHttpClientFactory(handler),
            TimeProvider.System,
            NullLogger<AgentNotificationSender>.Instance);
        var snapshot = CreateSnapshot(
            ghostDisconnectMessageTemplate: "first {pid}",
            disconnectNotificationEnabled: false,
            deliveryMode: "Cloud",
            backendBaseUrl: "https://backend.example.test",
            cloudAuthKey: "cloud-token");
        var monitorEvent = new MonitorEvent(
            new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc),
            4242,
            "Lineage II",
            MonitorEventKind.GhostDisconnectSuspected,
            0);

        var result = await sender.SendMonitorEventAsync(snapshot, monitorEvent);

        Assert.False(result.Delivered);
        Assert.True(result.Suppressed);
        Assert.Null(handler.LastFormBody);
        Assert.Contains("disabled by settings", result.Health.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendTestNotificationAsync_CloudPostsAuthenticatedEventToBackend()
    {
        var handler = new CaptureHandler();
        var sender = new AgentNotificationSender(
            new StubHttpClientFactory(handler),
            TimeProvider.System,
            NullLogger<AgentNotificationSender>.Instance);
        var snapshot = CreateSnapshot(
            ghostDisconnectMessageTemplate: null,
            deliveryMode: "Cloud",
            backendBaseUrl: "https://backend.example.test",
            cloudAuthKey: "agent-token");

        var result = await sender.SendTestNotificationAsync(snapshot);

        Assert.True(result.Delivered);
        Assert.Equal("Bearer", handler.LastAuthorizationScheme);
        Assert.Equal("agent-token", handler.LastAuthorizationParameter);
        Assert.Equal("https://backend.example.test/public/agent/events", handler.LastRequestUri);
        Assert.NotNull(handler.LastJsonBody);
        Assert.Contains("\"kind\":\"test_notification\"", handler.LastJsonBody!, StringComparison.Ordinal);
        Assert.Contains("\"machine_fingerprint\":", handler.LastJsonBody!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MonitorEventKind.ClientDisconnected, "client_disconnected")]
    [InlineData(MonitorEventKind.IdleBack, "client_reconnected")]
    [InlineData(MonitorEventKind.ProcessExited, "client_closed")]
    [InlineData(MonitorEventKind.DeadStarted, "character_death")]
    public async Task SendMonitorEventAsync_CloudMapsSupportedRuntimeEvents(
        MonitorEventKind kind,
        string expectedKind)
    {
        var handler = new CaptureHandler();
        var sender = new AgentNotificationSender(
            new StubHttpClientFactory(handler),
            TimeProvider.System,
            NullLogger<AgentNotificationSender>.Instance);
        var snapshot = CreateSnapshot(
            ghostDisconnectMessageTemplate: null,
            deliveryMode: "Cloud",
            backendBaseUrl: "https://backend.example.test",
            cloudAuthKey: "agent-token");
        var monitorEvent = new MonitorEvent(
            new DateTime(2026, 10, 4, 10, 0, 0, DateTimeKind.Utc),
            4242,
            "l2.bin",
            kind,
            kind == MonitorEventKind.IdleBack ? 30 : 0);

        var result = await sender.SendMonitorEventAsync(
            snapshot,
            monitorEvent,
            new Dictionary<string, string?> { ["windowTitle"] = "Lineage II" });

        Assert.True(result.Delivered);
        Assert.Contains($"\"kind\":\"{expectedKind}\"", handler.LastJsonBody!, StringComparison.Ordinal);
        Assert.Contains("\"process_id\":0", handler.LastJsonBody!, StringComparison.Ordinal);
        Assert.Contains("\"process_name\":\"Lineage II\"", handler.LastJsonBody!, StringComparison.Ordinal);
        Assert.Contains("\"window_title\":null", handler.LastJsonBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendTestNotificationAsync_TransportFailureDoesNotExposeBotToken()
    {
        const string botToken = "123456:super-secret-token";
        var logger = new CaptureLogger<AgentNotificationSender>();
        var factory = new StubHttpClientFactory(new ThrowingHandler(botToken));
        var sender = new AgentNotificationSender(
            factory,
            TimeProvider.System,
            logger);
        var snapshot = CreateSnapshot(ghostDisconnectMessageTemplate: null) with
        {
            Secrets = new AgentSecrets { TelegramBotToken = botToken },
        };

        var result = await sender.SendTestNotificationAsync(snapshot);

        Assert.False(result.Delivered);
        Assert.Equal(AgentNotificationSender.TelegramHttpClientName, factory.LastClientName);
        Assert.DoesNotContain(botToken, result.Health.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(botToken, string.Join(Environment.NewLine, logger.Messages), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMonitorEventText_BothDisconnectKindsUseVisibleTemplateAfterTrayUpdate()
    {
        var root = Path.Combine(Path.GetTempPath(), "l2monitor-agent-notification-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousAgentHome = Environment.GetEnvironmentVariable("L2MONITOR_AGENT_HOME");

        try
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", root);
            var configuration = new AgentConfigurationService();

            configuration.UpdateSettings(new LocalControlSettingsDto(
                LoopbackPort: 45631,
                PollIntervalMs: 1000,
                IdleTimeoutSec: 5,
                MinConfirmLifetimeSec: 0,
                DeliveryMode: "Local",
                BackendBaseUrl: null,
                TelegramDeliveryEnabled: true,
                TelegramChatId: 123,
                IgnoredWindowTitlesText: null,
                GhostDisconnectMessageTemplate: "legacy one-line",
                ClientDisconnectedMessageTemplate: "legacy hidden template",
                ProcessExitedMessageTemplate: null,
                DeadStartedMessageTemplate: null));

            var snapshot = configuration.UpdateTraySettings(new TrayEditableSettingsDto(
                DeliveryMode: "Local",
                TelegramDeliveryEnabled: true,
                IgnoredWindowTitlesText: null,
                GhostDisconnectMessageTemplate: "first {pid}\nsecond {name}",
                ProcessExitedMessageTemplate: null,
                DeadStartedMessageTemplate: null));

            foreach (var kind in new[] { MonitorEventKind.GhostDisconnectSuspected, MonitorEventKind.ClientDisconnected })
            {
                var monitorEvent = new MonitorEvent(
                    new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc),
                    4242,
                    "Lineage II",
                    kind,
                    0);

                var second = AgentNotificationSender.BuildMonitorEventText(snapshot, monitorEvent, "window", variantCount =>
                {
                    Assert.Equal(2, variantCount);
                    return 1;
                });

                Assert.Equal("second Lineage II", second);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("L2MONITOR_AGENT_HOME", previousAgentHome);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(MonitorEventKind.GhostDisconnectSuspected)]
    [InlineData(MonitorEventKind.ClientDisconnected)]
    public void BuildMonitorEventText_DivergentLegacyFieldsPreferVisibleDisconnectTemplate(MonitorEventKind kind)
    {
        var snapshot = CreateSnapshot(
            ghostDisconnectMessageTemplate: "видимый {name} ⚔️",
            clientDisconnectedMessageTemplate: "???? скрытый");
        var monitorEvent = new MonitorEvent(
            new DateTime(2026, 7, 9, 10, 0, 0, DateTimeKind.Utc),
            4242,
            "Lineage II",
            kind,
            0);

        var text = AgentNotificationSender.BuildMonitorEventText(snapshot, monitorEvent, "window", _ => 0);

        Assert.Equal("видимый Lineage II ⚔️", text);
    }

    private static AgentConfigurationSnapshot CreateSnapshot(
        string? ghostDisconnectMessageTemplate,
        string? clientDisconnectedMessageTemplate = null,
        bool disconnectNotificationEnabled = true,
        string deliveryMode = "Local",
        string? backendBaseUrl = null,
        string? cloudAuthKey = null) =>
        new(
            new AgentSettings
            {
                Delivery = new DeliverySettings
                {
                    Mode = deliveryMode,
                },
                Telegram = new TelegramSettings
                {
                    DeliveryEnabled = true,
                    ChatId = 123,
                },
                Cloud = new CloudSettings
                {
                    BackendBaseUrl = backendBaseUrl,
                },
                Notifications = new AgentNotificationSettings
                {
                    DisconnectNotificationEnabled = disconnectNotificationEnabled,
                    GhostDisconnectMessageTemplate = ghostDisconnectMessageTemplate,
                    ClientDisconnectedMessageTemplate = clientDisconnectedMessageTemplate,
                },
            },
            new AgentSecrets
            {
                TelegramBotToken = "token",
                CloudAuthKey = cloudAuthKey ?? string.Empty,
            });

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler = handler;

        public string? LastClientName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            LastClientName = name;
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? LastFormBody { get; private set; }
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }
        public string? LastRequestUri { get; private set; }
        public string? LastJsonBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            LastRequestUri = request.RequestUri?.ToString();
            LastJsonBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastFormBody = LastJsonBody;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ThrowingHandler(string botToken) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException($"Failed request {request.RequestUri}; token={botToken}");
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
            if (exception is not null)
            {
                Messages.Add(exception.ToString());
            }
        }
    }
}
