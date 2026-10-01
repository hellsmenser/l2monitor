using L2Monitor.Infrastructure.Windows;

namespace L2Monitor.Agent.Runtime;

internal sealed record AgentRuntimeProbeSnapshot(
    DateTimeOffset CapturedAtUtc,
    int CandidateProcessCount,
    int GameConnectionCount,
    IReadOnlyList<AgentRuntimeConnectionSnapshot> Connections);

internal sealed record AgentRuntimeConnectionSnapshot(
    string Id,
    string ProcessName,
    string? WindowTitle,
    string State,
    DateTimeOffset ObservedAtUtc,
    AgentConnectionForensics? Forensics = null);
