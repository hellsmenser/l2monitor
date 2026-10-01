namespace L2Monitor.Infrastructure.Windows;

internal sealed class NoOpWindowsMonitorProbe(TimeProvider timeProvider) : IWindowsMonitorProbe
{
    private readonly TimeProvider _timeProvider = timeProvider;

    public Task<WindowsMonitorProbeResult> ProbeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new WindowsMonitorProbeResult(
            CapturedAtUtc: _timeProvider.GetUtcNow(),
            CandidateProcessCount: 0,
            GameConnectionCount: 0,
            Connections: []));
    }
}
