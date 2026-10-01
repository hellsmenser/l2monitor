using L2Monitor.Infrastructure.Windows;

namespace L2Monitor.Agent.Runtime;

internal sealed class WindowsMonitorProbeAdapter(IWindowsMonitorProbe probe) : IAgentRuntimeProbe
{
    private readonly IWindowsMonitorProbe _probe = probe;

    public async Task<AgentRuntimeProbeSnapshot> ProbeAsync(CancellationToken cancellationToken)
    {
        var probeResult = await _probe.ProbeAsync(cancellationToken).ConfigureAwait(false);

        return new AgentRuntimeProbeSnapshot(
            CapturedAtUtc: probeResult.CapturedAtUtc,
            CandidateProcessCount: probeResult.CandidateProcessCount,
            GameConnectionCount: probeResult.GameConnectionCount,
            Connections: probeResult.Connections
                .Select(connection => new AgentRuntimeConnectionSnapshot(
                    connection.Id,
                    connection.ProcessName,
                    connection.WindowTitle,
                    connection.State,
                    connection.ObservedAtUtc,
                    connection.Forensics))
                .ToArray());
    }
}
