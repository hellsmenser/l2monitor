namespace L2Monitor.Infrastructure.Windows;

public interface IWindowsMonitorProbe
{
    Task<WindowsMonitorProbeResult> ProbeAsync(CancellationToken cancellationToken);
}
