namespace L2Monitor.Infrastructure.Windows;

public sealed record WindowsMonitorProbeResult(
    DateTimeOffset CapturedAtUtc,
    int CandidateProcessCount,
    int GameConnectionCount,
    IReadOnlyList<WindowsMonitorConnectionRecord> Connections);

public sealed record WindowsMonitorConnectionRecord(
    string Id,
    string ProcessName,
    string? WindowTitle,
    string State,
    DateTimeOffset ObservedAtUtc,
    AgentConnectionForensics? Forensics = null);

public sealed record AgentConnectionForensics(
    string Kind,
    string Summary,
    int EstablishedRowCount,
    IReadOnlyList<int> ObservedRemotePorts);
