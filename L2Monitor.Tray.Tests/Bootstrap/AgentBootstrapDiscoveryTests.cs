using L2Monitor.Core.Storage;
using L2Monitor.Tray.Bootstrap;
using Xunit;

namespace L2Monitor.Tray.Tests.Bootstrap;

public sealed class AgentBootstrapDiscoveryTests
{
    [Fact]
    public void Load_ReadsLoopbackPortAndBearerTokenFromBootstrapStore()
    {
        var root = CreateTempRoot();

        try
        {
            AgentBootstrapStore.Save(root, new AgentBootstrapRecord(45642, "tray-test-token"));

            var discovery = new AgentBootstrapDiscovery(root);
            var snapshot = discovery.Load();

            Assert.Equal("http://127.0.0.1:45642/", snapshot.BaseAddress.ToString());
            Assert.Equal("tray-test-token", snapshot.LocalApiToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_UsesDefaultPortWhenSettingsAreMissing()
    {
        var root = CreateTempRoot();

        try
        {
            var discovery = new AgentBootstrapDiscovery(root);
            var snapshot = discovery.Load();

            Assert.Equal("http://127.0.0.1:45631/", snapshot.BaseAddress.ToString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_FallsBackToDefaultPortWhenBootstrapStoreIsUnreadable()
    {
        var root = CreateTempRoot();

        try
        {
            File.WriteAllBytes(Path.Combine(root, AgentStorageLayout.BootstrapFileName), [0x01, 0x02, 0x03]);

            var discovery = new AgentBootstrapDiscovery(root);
            var snapshot = discovery.Load();

            Assert.Equal("http://127.0.0.1:45631/", snapshot.BaseAddress.ToString());
            Assert.Null(snapshot.LocalApiToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "l2monitor-tray-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
