namespace L2Monitor.Agent.Runtime;

internal sealed class AgentRuntimeStateStore
{
    private readonly object _sync = new();
    private AgentRuntimeState _current = new(
        Status: AgentRuntimeStatus.Starting,
        StartedAtUtc: DateTimeOffset.MinValue,
        LastProbeCompletedAtUtc: null,
        LastError: null,
        ProbeCount: 0,
        LastObservedProcessCount: 0,
        LastObservedConnectionCount: 0,
        Connections: []);

    public AgentRuntimeState GetSnapshot()
    {
        lock (_sync)
        {
            return _current;
        }
    }

    public void Update(AgentRuntimeState next)
    {
        lock (_sync)
        {
            _current = next;
        }
    }
}
