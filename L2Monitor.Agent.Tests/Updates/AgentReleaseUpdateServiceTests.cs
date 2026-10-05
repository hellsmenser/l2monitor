using L2Monitor.Agent.Runtime;
using L2Monitor.Agent.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace L2Monitor.Agent.Tests.Updates;

public sealed class AgentReleaseUpdateServiceTests
{
    [Fact]
    public void EndpointProvider_PrefersPackagedBackendAndAllowsLoopbackDevelopmentFallback()
    {
        Assert.Equal(
            "https://service.example/",
            AgentUpdateEndpointProvider.ResolveBackendBaseUri(
                "https://service.example/",
                "https://stale-user-setting.example/")?.AbsoluteUri);
        Assert.Equal(
            "http://127.0.0.1:18080/",
            AgentUpdateEndpointProvider.ResolveBackendBaseUri(
                null,
                "http://127.0.0.1:18080/")?.AbsoluteUri);
        Assert.Null(AgentUpdateEndpointProvider.ResolveBackendBaseUri(
            null,
            "http://public-insecure.example/"));
    }

    [Fact]
    public async Task Service_PublishesAvailableReleaseWithoutAnyDeliveryDependency()
    {
        var state = new AgentControlStateStore();
        var checker = new StubChecker();
        var service = new AgentReleaseUpdateService(
            new StubEndpointProvider(new Uri("https://service.example/")),
            checker,
            state,
            TimeProvider.System,
            NullLogger<AgentReleaseUpdateService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            var timeout = DateTime.UtcNow.AddSeconds(2);
            while (state.GetLastUpdate() is null && DateTime.UtcNow < timeout)
            {
                await Task.Delay(10);
            }

            var update = Assert.IsType<AgentUpdateStateRecord>(state.GetLastUpdate());
            Assert.True(update.IsUpdateAvailable);
            Assert.True(update.Required);
            Assert.Equal("1.0.2", update.LatestVersion);
            Assert.Equal("https://service.example/", checker.BackendBaseUri?.AbsoluteUri);
            Assert.Equal(1, checker.CallCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    private sealed class StubChecker : IAgentReleaseUpdateChecker
    {
        public int CallCount { get; private set; }
        public Uri? BackendBaseUri { get; private set; }

        public Task<AgentUpdateCheckResult> CheckAsync(
            Uri backendBaseUri,
            string currentVersion,
            CancellationToken cancellationToken)
        {
            CallCount++;
            BackendBaseUri = backendBaseUri;
            return Task.FromResult(new AgentUpdateCheckResult(
                "available",
                currentVersion,
                "1.0.2",
                true,
                true,
                "https://github.com/hellsmenser/l2monitor/releases/tag/v1.0.2",
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class StubEndpointProvider(Uri? backendBaseUri) : IAgentUpdateEndpointProvider
    {
        public Uri? GetBackendBaseUri() => backendBaseUri;
    }
}
