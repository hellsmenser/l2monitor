namespace L2Monitor.Agent.Runtime;

internal sealed record AgentRuntimeOptions(TimeSpan PollInterval)
{
    public static AgentRuntimeOptions Default { get; } =
        new(TimeSpan.FromSeconds(30));
}
