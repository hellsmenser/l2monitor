using L2Monitor.Agent.Hosting;
using Microsoft.Extensions.Hosting;

namespace L2Monitor.Agent;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        var restartParent = AgentRestartSupport.ParseRestartParent(args, out var filteredArgs);
        AgentRestartSupport.WaitForParentExit(restartParent);

        using var host = AgentHostBuilder.Build(filteredArgs);
        await host.RunAsync().ConfigureAwait(false);
    }
}
