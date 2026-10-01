using L2Monitor.Tray.Bootstrap;
using Xunit;

namespace L2Monitor.Tray.Tests.Bootstrap;

public sealed class AgentProcessLauncherTests
{
    [Fact]
    public void ResolveExecutablePath_PrefersPackagedAgent()
    {
        var root = Path.Combine(Path.GetTempPath(), "adenplus-launcher-tests", Guid.NewGuid().ToString("N"));
        var packagedAgent = Path.Combine(root, "runtime", "agent", "AdenPlus.Agent.exe");
        var legacyAgent = Path.Combine(root, "agent", "L2Monitor.Agent.exe");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(packagedAgent)!);
            Directory.CreateDirectory(Path.GetDirectoryName(legacyAgent)!);
            File.WriteAllBytes(packagedAgent, []);
            File.WriteAllBytes(legacyAgent, []);

            Assert.Equal(packagedAgent, AgentProcessLauncher.ResolveExecutablePath(root));
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
    public void CreateStartInfo_UsesHiddenChildProcessSettings()
    {
        var executablePath = Path.Combine(Path.GetTempPath(), "AdenPlus.Agent.exe");

        var startInfo = AgentProcessLauncher.CreateStartInfo(executablePath);

        Assert.Equal(executablePath, startInfo.FileName);
        Assert.Equal(Path.GetDirectoryName(executablePath), startInfo.WorkingDirectory);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(System.Diagnostics.ProcessWindowStyle.Hidden, startInfo.WindowStyle);
    }
}
