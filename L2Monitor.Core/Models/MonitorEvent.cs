namespace L2Monitor.Core.Models;

public sealed record MonitorEvent(DateTime Time, int Pid, string Name, MonitorEventKind Kind, int DurationSec);
