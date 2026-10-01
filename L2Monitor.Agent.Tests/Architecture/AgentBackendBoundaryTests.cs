using L2Monitor.Agent.Backend;
using L2Monitor.Agent.Hosting;
using Xunit;

namespace L2Monitor.Agent.Tests.Architecture;

public sealed class AgentBackendBoundaryTests
{
    [Fact]
    public void CloudConnection_UsesOnlyVersionEndpointAndAgentWebSocket()
    {
        var snapshot = new AgentConfigurationSnapshot(
            AgentSettings.Default with
            {
                Delivery = new DeliverySettings { Mode = "Cloud" },
                Cloud = new CloudSettings { BackendBaseUrl = "https://backend.example.test/base" },
            },
            AgentSecrets.Empty with { CloudAuthKey = "agent-key" });

        var resolved = AgentBackendConnectionService.TryResolveCloudConnection(
            snapshot,
            out var backendUri,
            out var webSocketUri,
            out var authKey,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal("https://backend.example.test/base", backendUri.AbsoluteUri.TrimEnd('/'));
        Assert.Equal("wss://backend.example.test/ws/agent", webSocketUri.AbsoluteUri);
        Assert.Equal("agent-key", authKey);
    }

    [Fact]
    public void AgentAndTraySources_DoNotContainRemovedHttpRelayEndpoints()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoots = new[]
        {
            Path.Combine(repositoryRoot, "L2Monitor.Agent"),
            Path.Combine(repositoryRoot, "L2Monitor.Tray"),
            Path.Combine(repositoryRoot, "L2Monitor.Core"),
        };
        var forbidden = new[]
        {
            "/api/v1/messages",
            "/api/v1/ping",
        };

        var violations = sourceRoots
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => Path.GetExtension(path) is ".cs" or ".csproj" or ".xaml" or ".py")
            .SelectMany(path => forbidden
                .Where(term => File.ReadAllText(path).Contains(term, StringComparison.OrdinalIgnoreCase))
                .Select(term => $"{path}: {term}"))
            .ToArray();

        Assert.Empty(violations);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "L2Monitor.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("L2Monitor.slnx was not found above the test output directory.");
    }
}
