namespace L2Monitor.Core.Models;

public enum MonitorEventKind
{
    TestNotification,
    GhostDisconnectSuspected,
    ClientDisconnected,
    DeadStarted,
    TimeoutExceeded,
    IdleBack,
    ProcessExited,
    ProcessExitedWhileDead,
}
