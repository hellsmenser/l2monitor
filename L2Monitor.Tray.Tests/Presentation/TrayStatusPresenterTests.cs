using L2Monitor.Core.Api;
using L2Monitor.Tray.Api;
using L2Monitor.Tray.Bootstrap;
using L2Monitor.Tray.Presentation;
using Xunit;

namespace L2Monitor.Tray.Tests.Presentation;

public sealed class TrayStatusPresenterTests
{
    [Fact]
    public void FromDashboard_UsesInfoSeverityWhenAgentIsConnectedWithActiveClients()
    {
        var summary = TrayStatusPresenter.FromDashboard(CreateDashboard(
            currentMode: "Local",
            activeConnections: 2,
            deliveryState: "healthy",
            backendState: "disabled",
            deliverySummary: "Telegram local delivery is configured.",
            backendSummary: "Backend health applies only in Cloud mode.",
            incidentSeverity: null));

        Assert.Equal(TraySeverity.Info, summary.Severity);
        Assert.True(summary.IsAgentAvailable);
    }

    [Fact]
    public void FromDashboard_UsesErrorSeverityForMisconfiguredHealth()
    {
        var summary = TrayStatusPresenter.FromDashboard(CreateDashboard(
            currentMode: "Cloud",
            activeConnections: 0,
            deliveryState: "misconfigured",
            backendState: "not_configured",
            deliverySummary: "Cloud delivery requires BackendBaseUrl to be an absolute http(s) URL.",
            backendSummary: "Cloud delivery requires a stored auth key.",
            incidentSeverity: "warning"));

        Assert.Equal(TraySeverity.Error, summary.Severity);
    }

    [Fact]
    public void FromDashboard_UsesWarningSeverityWhenIncidentIsWarningWithoutErrorHealth()
    {
        var summary = TrayStatusPresenter.FromDashboard(CreateDashboard(
            currentMode: "Local",
            activeConnections: 0,
            deliveryState: "healthy",
            backendState: "disabled",
            deliverySummary: "Telegram local delivery is configured.",
            backendSummary: "Backend health applies only in Cloud mode.",
            incidentSeverity: "warning"));

        Assert.Equal(TraySeverity.Warning, summary.Severity);
        Assert.True(summary.IsAgentAvailable);
    }

    [Fact]
    public void Offline_ReturnsUnavailableSummary()
    {
        var summary = TrayStatusPresenter.Offline("Connection refused.");

        Assert.Equal(TraySeverity.Error, summary.Severity);
        Assert.False(summary.IsAgentAvailable);
    }

    [Fact]
    public void Offline_PreservesLastKnownEvidenceWhenAvailable()
    {
        var summary = TrayStatusPresenter.Offline(
            "Connection refused.",
            CreateDashboard(
                currentMode: "Local",
                activeConnections: 3,
                deliveryState: "healthy",
                backendState: "disabled",
                deliverySummary: "Telegram local delivery is configured.",
                backendSummary: "Backend health applies only in Cloud mode.",
                incidentSeverity: null));

        Assert.Equal(TraySeverity.Error, summary.Severity);
        Assert.False(summary.IsAgentAvailable);
        Assert.Contains("3", summary.Headline, StringComparison.Ordinal);
    }

    private static TrayDashboardSnapshot CreateDashboard(
        string currentMode,
        int activeConnections,
        string deliveryState,
        string backendState,
        string deliverySummary,
        string backendSummary,
        string? incidentSeverity)
    {
        return new TrayDashboardSnapshot(
            new AgentBootstrapSnapshot(new Uri("http://127.0.0.1:45631/"), "token"),
            new StatusResponseDto(
                new ApiVersionDto("1.0", "1.0"),
                "Running",
                currentMode,
                45631,
                activeConnections,
                5,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                new ComponentHealthDto(deliveryState, deliverySummary),
                new ComponentHealthDto(backendState, backendSummary),
                incidentSeverity is null ? null : new AgentIncidentDto("1", "incident", incidentSeverity, "summary", DateTimeOffset.UtcNow),
                null,
                Array.Empty<AgentConnectionDto>()),
            new SettingsResponseDto(
                new ApiVersionDto("1.0", "1.0"),
                45631,
                new LocalControlSettingsDto(45631, 30000, 5, 5, currentMode, null, false, null),
                new SecretPresenceDto(true, false, false)),
            new IncidentsResponseDto(new ApiVersionDto("1.0", "1.0"), Array.Empty<AgentIncidentDto>()));
    }
}
