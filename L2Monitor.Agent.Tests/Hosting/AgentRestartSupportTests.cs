using L2Monitor.Agent.Hosting;
using Xunit;

namespace L2Monitor.Agent.Tests.Hosting;

public sealed class AgentRestartSupportTests
{
    [Fact]
    public void ParseRestartParent_StripsFlagAndReturnsPid()
    {
        var restartParent = AgentRestartSupport.ParseRestartParent(
            ["--run-once", "--restart-parent", "4242"],
            out var filteredArgs);

        Assert.Equal(4242, restartParent);
        Assert.Equal(["--run-once"], filteredArgs);
    }

    [Fact]
    public void TryCreateRelaunchPlan_UsesDotnetHostForFrameworkDependentLaunch()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "l2monitor-agent-restart-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var dotnetPath = Path.Combine(tempRoot, "dotnet.exe");
            var assemblyPath = Path.Combine(tempRoot, "L2Monitor.Agent.dll");
            File.WriteAllText(dotnetPath, string.Empty);
            File.WriteAllText(assemblyPath, string.Empty);

            var plan = AgentRestartSupport.TryCreateRelaunchPlan(
                dotnetPath,
                assemblyPath,
                ["--run-once"],
                restartParentPid: 9001);

            Assert.NotNull(plan);
            Assert.Equal(dotnetPath, plan!.FileName);
            Assert.Equal(Path.GetDirectoryName(assemblyPath), plan.WorkingDirectory);
            Assert.Equal([assemblyPath, "--run-once", "--restart-parent", "9001"], plan.Arguments);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
