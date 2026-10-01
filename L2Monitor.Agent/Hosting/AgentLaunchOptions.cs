namespace L2Monitor.Agent.Hosting;

internal sealed record AgentLaunchOptions(bool RunOnce, IReadOnlyList<string>? ForwardedArgs = null)
{
    public static AgentLaunchOptions Parse(string[] args)
    {
        var runOnce = args.Any(arg => string.Equals(arg, "--run-once", StringComparison.OrdinalIgnoreCase));
        return new AgentLaunchOptions(runOnce, args);
    }
}
