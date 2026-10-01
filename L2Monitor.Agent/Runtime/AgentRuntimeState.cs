namespace L2Monitor.Agent.Runtime;

internal sealed record AgentRuntimeState(
    AgentRuntimeStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? LastProbeCompletedAtUtc,
    string? LastError,
    int ProbeCount,
    int LastObservedProcessCount,
    int LastObservedConnectionCount,
    IReadOnlyList<AgentConnectionRecord> Connections);

internal enum AgentRuntimeStatus
{
    Starting,
    Running,
    Stopped,
    Faulted,
}
