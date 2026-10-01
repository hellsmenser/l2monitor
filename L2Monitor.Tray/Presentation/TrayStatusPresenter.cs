using L2Monitor.Tray.Api;

namespace L2Monitor.Tray.Presentation;

internal static class TrayStatusPresenter
{
    public static TrayPresentationSummary FromDashboard(TrayDashboardSnapshot snapshot)
    {
        var healthStates = new[]
        {
            snapshot.Status.Delivery.State,
            snapshot.Status.Backend.State,
            snapshot.Status.LastIncident?.Severity,
        };

        var severity = healthStates.Any(TrayStateClassifier.IsErrorState)
                ? TraySeverity.Error
                : healthStates.Any(TrayStateClassifier.AffectsTraySeverityAsWarning)
                    ? TraySeverity.Warning
                    : snapshot.Status.ActiveConnectionCount > 0
                        ? TraySeverity.Info
                        : TraySeverity.Normal;

        var tooltip = $"Aden+: {DescribeMode(snapshot.Status.CurrentMode)}, клиентов: {snapshot.Status.ActiveConnectionCount}";
        var headline = $"{DescribeMode(snapshot.Status.CurrentMode)} · Клиенты: {snapshot.Status.ActiveConnectionCount}";
        var detail = $"Доставка: {TrayLabelCanon.DescribeHealthState(snapshot.Status.Delivery.State)}; " +
                     $"Облако: {TrayLabelCanon.DescribeHealthState(snapshot.Status.Backend.State)}";

        return new TrayPresentationSummary(tooltip, headline, detail, severity, IsAgentAvailable: true);
    }

    public static TrayPresentationSummary Offline(string detail, TrayDashboardSnapshot? lastKnownSnapshot = null)
    {
        if (lastKnownSnapshot is null)
        {
            return new(
                "Aden+: агент недоступен",
                "Агент недоступен",
                detail,
                TraySeverity.Error,
                IsAgentAvailable: false);
        }

        var mode = DescribeMode(lastKnownSnapshot.Status.CurrentMode);
        var count = lastKnownSnapshot.Status.ActiveConnectionCount;
        return new(
            $"Aden+: агент недоступен; последний режим: {mode.ToLowerInvariant()}, клиентов: {count}",
            $"Агент недоступен · Последних клиентов: {count}",
            $"Последний режим: {mode}. {detail}",
            TraySeverity.Error,
            IsAgentAvailable: false);
    }

    private static string DescribeMode(string? mode) =>
        string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase)
            ? "Локально"
            : string.Equals(mode, "Cloud", StringComparison.OrdinalIgnoreCase)
                ? "Облако"
                : string.Equals(mode, "Disabled", StringComparison.OrdinalIgnoreCase)
                    ? "Доставка выключена"
                    : "Неизвестный режим";
}

internal sealed record TrayPresentationSummary(
    string Tooltip,
    string Headline,
    string Detail,
    TraySeverity Severity,
    bool IsAgentAvailable);

internal enum TraySeverity
{
    Normal,
    Info,
    Warning,
    Error,
}
