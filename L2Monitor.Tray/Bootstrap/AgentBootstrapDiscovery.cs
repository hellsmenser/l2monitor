using L2Monitor.Core.Storage;

namespace L2Monitor.Tray.Bootstrap;

internal class AgentBootstrapDiscovery
{
    private readonly string? _bootstrapDirectoryOverride;

    public AgentBootstrapDiscovery(string? bootstrapDirectoryOverride = null)
    {
        _bootstrapDirectoryOverride = bootstrapDirectoryOverride;
    }

    public virtual AgentBootstrapSnapshot Load()
    {
        var bootstrap = string.IsNullOrWhiteSpace(_bootstrapDirectoryOverride)
            ? AgentBootstrapStore.TryLoad()
            : AgentBootstrapStore.TryLoad(_bootstrapDirectoryOverride);
        var port = bootstrap?.LoopbackPort is >= 1024 and <= 65535
            ? bootstrap.LoopbackPort
            : 45631;
        var token = string.IsNullOrWhiteSpace(bootstrap?.LocalApiToken)
            ? null
            : bootstrap.LocalApiToken;

        return new AgentBootstrapSnapshot(
            new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute),
            token);
    }
}

internal sealed record AgentBootstrapSnapshot(
    Uri BaseAddress,
    string? LocalApiToken);
