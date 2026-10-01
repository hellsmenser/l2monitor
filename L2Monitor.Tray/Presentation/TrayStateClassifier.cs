namespace L2Monitor.Tray.Presentation;

internal static class TrayStateClassifier
{
    public static bool AffectsTraySeverityAsWarning(string? state) =>
        string.Equals(state, "warning", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "degraded", StringComparison.OrdinalIgnoreCase);

    public static bool IsHealthyState(string? state) =>
        string.Equals(state, "healthy", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "running", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "available", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "ok", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "configured", StringComparison.OrdinalIgnoreCase);

    public static bool IsWarningState(string? state) =>
        string.Equals(state, "warning", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "degraded", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "disabled", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "idle", StringComparison.OrdinalIgnoreCase);

    public static bool IsErrorState(string? state) =>
        string.Equals(state, "unreachable", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "misconfigured", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "auth_failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "protocol_error", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "rate_limited", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "not_configured", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "error", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "unavailable", StringComparison.OrdinalIgnoreCase);
}
