using System.Net;
using System.Text;
using System.Text.Json;
using L2Monitor.Core.Api;
using L2Monitor.Tray.Api;
using L2Monitor.Tray.Bootstrap;
using Xunit;

namespace L2Monitor.Tray.Tests.Api;

public sealed class LocalControlApiClientTests
{
    [Fact]
    public async Task UpdateTraySettingsAsync_KeepsUsingLastKnownActiveEndpointUntilRediscoveryIsNeeded()
    {
        var discovery = new StubBootstrapDiscovery(
            new AgentBootstrapSnapshot(new Uri("http://127.0.0.1:45631/"), "token"),
            new AgentBootstrapSnapshot(new Uri("http://127.0.0.1:45642/"), "token"));
        var handler = new SequencedHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var port = request.RequestUri.Port;

            return (path, port) switch
            {
                ("/v1/settings/tray", 45631) when request.Method == HttpMethod.Put
                    => JsonResponse(CreateSettingsResponse(activeLoopbackPort: 45631, configuredLoopbackPort: 45642)),
                ("/v1/actions/reload", 45631) when request.Method == HttpMethod.Post
                    => JsonResponse(new ActionResponseDto(
                        new ApiVersionDto("1.0", "1.0"),
                        new AgentActionResultDto("reload", "completed", "ok", DateTimeOffset.UtcNow))),
                ("/v1/actions/reload", 45642) when request.Method == HttpMethod.Post
                    => JsonResponse(new ActionResponseDto(
                        new ApiVersionDto("1.0", "1.0"),
                        new AgentActionResultDto("reload", "completed", "rebound", DateTimeOffset.UtcNow))),
                _ => throw new HttpRequestException($"No stub response for {request.Method} {request.RequestUri}")
            };
        });
        var client = new LocalControlApiClient(new HttpClient(handler), discovery);

        await client.UpdateTraySettingsAsync(
            new UpdateTraySettingsRequestDto(new TrayEditableSettingsDto("Disabled", false, null, null, null)),
            CancellationToken.None);

        await client.TriggerReloadAsync(CancellationToken.None);
        handler.FailEndpoint(45631);
        await client.TriggerReloadAsync(CancellationToken.None);

        Assert.Equal(
            [45631, 45631, 45631, 45642],
            handler.Requests.Select(static request => request.RequestUri!.Port).ToArray());
        Assert.Equal(2, discovery.LoadCallCount);
    }

    [Fact]
    public async Task GetDashboardAsync_UpdatesCachedEndpointFromActiveLoopbackPort()
    {
        var discovery = new StubBootstrapDiscovery(
            new AgentBootstrapSnapshot(new Uri("http://127.0.0.1:45631/"), "token"));
        var handler = new SequencedHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var port = request.RequestUri.Port;

            return (path, port) switch
            {
                ("/v1/status", 45631) => JsonResponse(new StatusResponseDto(
                    new ApiVersionDto("1.0", "1.0"),
                    "Running",
                    "Disabled",
                    45642,
                    0,
                    0,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    new ComponentHealthDto("disabled", "disabled"),
                    new ComponentHealthDto("disabled", "disabled"),
                    null,
                    null,
                    Array.Empty<AgentConnectionDto>())),
                ("/v1/settings", 45631) => JsonResponse(CreateSettingsResponse(activeLoopbackPort: 45642, configuredLoopbackPort: 45642)),
                ("/v1/incidents", 45631) => JsonResponse(new IncidentsResponseDto(
                    new ApiVersionDto("1.0", "1.0"),
                    Array.Empty<AgentIncidentDto>())),
                ("/v1/actions/reload", 45642) => JsonResponse(new ActionResponseDto(
                    new ApiVersionDto("1.0", "1.0"),
                    new AgentActionResultDto("reload", "completed", "ok", DateTimeOffset.UtcNow))),
                _ => throw new HttpRequestException($"No stub response for {request.Method} {request.RequestUri}")
            };
        });
        var client = new LocalControlApiClient(new HttpClient(handler), discovery);

        var dashboard = await client.GetDashboardAsync(CancellationToken.None);
        await client.TriggerReloadAsync(CancellationToken.None);

        Assert.Equal(45642, dashboard.Bootstrap.BaseAddress.Port);
        Assert.Equal(
            [45631, 45631, 45631, 45642],
            handler.Requests.Select(static request => request.RequestUri!.Port).ToArray());
        Assert.Equal(1, discovery.LoadCallCount);
    }

    [Fact]
    public async Task GetDashboardAsync_RediscoveryHealsStaleCachedBootstrapForLaterRefreshes()
    {
        var discovery = new StubBootstrapDiscovery(
            new AgentBootstrapSnapshot(new Uri("http://127.0.0.1:45631/"), "token-old"),
            new AgentBootstrapSnapshot(new Uri("http://127.0.0.1:45642/"), "token-new"));
        var handler = new SequencedHandler(
            request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                var port = request.RequestUri.Port;
                var token = request.Headers.Authorization?.Parameter;

                return (path, port, token) switch
                {
                    ("/v1/status", 45631, "token-old") => ErrorResponse(HttpStatusCode.Unauthorized, "unauthorized", "stale token"),
                    ("/v1/settings", 45631, "token-old") => ErrorResponse(HttpStatusCode.Unauthorized, "unauthorized", "stale token"),
                    ("/v1/incidents", 45631, "token-old") => ErrorResponse(HttpStatusCode.Unauthorized, "unauthorized", "stale token"),
                    ("/v1/status", 45642, "token-new") => JsonResponse(new StatusResponseDto(
                        new ApiVersionDto("1.0", "1.0"),
                        "Running",
                        "Disabled",
                        45642,
                        0,
                        0,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        new ComponentHealthDto("disabled", "disabled"),
                        new ComponentHealthDto("disabled", "disabled"),
                        null,
                        null,
                        Array.Empty<AgentConnectionDto>())),
                    ("/v1/settings", 45642, "token-new") => JsonResponse(CreateSettingsResponse(activeLoopbackPort: 45642, configuredLoopbackPort: 45642)),
                    ("/v1/incidents", 45642, "token-new") => JsonResponse(new IncidentsResponseDto(
                        new ApiVersionDto("1.0", "1.0"),
                        Array.Empty<AgentIncidentDto>())),
                    _ => throw new HttpRequestException($"No stub response for {request.Method} {request.RequestUri} ({token})")
                };
            },
            request =>
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Contains(LocalControlApiContract.CurrentVersion, request.Headers.GetValues(LocalControlApiContract.VersionHeaderName));
            });
        var client = new LocalControlApiClient(new HttpClient(handler), discovery);

        var firstDashboard = await client.GetDashboardAsync(CancellationToken.None);
        var secondDashboard = await client.GetDashboardAsync(CancellationToken.None);

        Assert.Equal(45642, firstDashboard.Bootstrap.BaseAddress.Port);
        Assert.Equal(45642, secondDashboard.Bootstrap.BaseAddress.Port);
        Assert.Equal(
            [45631, 45631, 45631, 45642, 45642, 45642, 45642, 45642, 45642],
            handler.Requests.Select(static request => request.RequestUri!.Port).ToArray());
        Assert.Equal(
            ["token-old", "token-old", "token-old", "token-new", "token-new", "token-new", "token-new", "token-new", "token-new"],
            handler.Requests.Select(static request => request.Headers.Authorization!.Parameter!).ToArray());
        Assert.Equal(2, discovery.LoadCallCount);
    }

    [Fact]
    public async Task RecoveryRequests_OmitAuthorizationWhenBootstrapTokenIsUnavailable()
    {
        var discovery = new StubBootstrapDiscovery(
            new AgentBootstrapSnapshot(new Uri("http://127.0.0.1:45631/"), null));
        var handler = new SequencedHandler(
            request =>
            {
                var path = request.RequestUri!.AbsolutePath;

                return path switch
                {
                    "/v1/status" => JsonResponse(new StatusResponseDto(
                        new ApiVersionDto("1.0", "1.0"),
                        "Running",
                        "Disabled",
                        45631,
                        0,
                        0,
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow,
                        new ComponentHealthDto("disabled", "disabled"),
                        new ComponentHealthDto("disabled", "disabled"),
                        null,
                        null,
                        Array.Empty<AgentConnectionDto>())),
                    "/v1/settings" => request.Method == HttpMethod.Get
                        ? JsonResponse(CreateSettingsResponse(activeLoopbackPort: 45631, configuredLoopbackPort: 45631))
                        : JsonResponse(CreateSettingsResponse(activeLoopbackPort: 45631, configuredLoopbackPort: 45631)),
                    "/v1/incidents" => JsonResponse(new IncidentsResponseDto(
                        new ApiVersionDto("1.0", "1.0"),
                        Array.Empty<AgentIncidentDto>())),
                    "/v1/settings/secrets/telegram" => JsonResponse(CreateSettingsResponse(activeLoopbackPort: 45631, configuredLoopbackPort: 45631)),
                    _ => throw new HttpRequestException($"No stub response for {request.Method} {request.RequestUri}")
                };
            },
            request =>
            {
                Assert.Null(request.Headers.Authorization);
                Assert.Contains(LocalControlApiContract.CurrentVersion, request.Headers.GetValues(LocalControlApiContract.VersionHeaderName));
            });
        var client = new LocalControlApiClient(new HttpClient(handler), discovery);

        await client.GetDashboardAsync(CancellationToken.None);
        await client.UpdateTelegramSecretAsync(new UpdateTelegramSecretRequestDto("new-token"), CancellationToken.None);

        Assert.Equal(
            ["/v1/status", "/v1/settings", "/v1/incidents", "/v1/settings/secrets/telegram"],
            handler.Requests.Select(static request => request.RequestUri!.AbsolutePath).ToArray());
    }

    private static SettingsResponseDto CreateSettingsResponse(int activeLoopbackPort, int configuredLoopbackPort) =>
        new(
            new ApiVersionDto("1.0", "1.0"),
            activeLoopbackPort,
            new LocalControlSettingsDto(configuredLoopbackPort, 1000, 5, 0, "Disabled", null, false, null),
            new SecretPresenceDto(true, false, false));

    private static HttpResponseMessage JsonResponse<T>(T payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode, string errorCode, string message)
    {
        var json = JsonSerializer.Serialize(new ErrorResponseDto(new ApiVersionDto("1.0", "1.0"), errorCode, message));
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubBootstrapDiscovery : AgentBootstrapDiscovery
    {
        private readonly Queue<AgentBootstrapSnapshot> _snapshots;

        public StubBootstrapDiscovery(params AgentBootstrapSnapshot[] snapshots)
            : base("unused")
        {
            _snapshots = new Queue<AgentBootstrapSnapshot>(snapshots);
        }

        public int LoadCallCount { get; private set; }

        public override AgentBootstrapSnapshot Load()
        {
            LoadCallCount++;
            if (_snapshots.Count == 0)
            {
                throw new InvalidOperationException("No more bootstrap snapshots are available.");
            }

            return _snapshots.Count > 1 ? _snapshots.Dequeue() : _snapshots.Peek();
        }
    }

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;
        private readonly Action<HttpRequestMessage> _requestAssertion;
        private readonly HashSet<int> _failedPorts = [];

        public SequencedHandler(
            Func<HttpRequestMessage, HttpResponseMessage> responseFactory,
            Action<HttpRequestMessage>? requestAssertion = null)
        {
            _responseFactory = responseFactory;
            _requestAssertion = requestAssertion ?? AssertDefaultHeaders;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public void FailEndpoint(int port) => _failedPorts.Add(port);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(CloneRequest(request));

            if (_failedPorts.Contains(request.RequestUri!.Port))
            {
                throw new HttpRequestException($"Endpoint {request.RequestUri.Port} is unavailable.");
            }

            _requestAssertion(request);

            return Task.FromResult(_responseFactory(request));
        }

        private static void AssertDefaultHeaders(HttpRequestMessage request)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("token", request.Headers.Authorization?.Parameter);
            Assert.Contains(LocalControlApiContract.CurrentVersion, request.Headers.GetValues(LocalControlApiContract.VersionHeaderName));
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }
}
