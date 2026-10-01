namespace L2Monitor.Agent.Runtime;

internal interface IAgentRuntimeProbe
{
    Task<AgentRuntimeProbeSnapshot> ProbeAsync(CancellationToken cancellationToken);
}
