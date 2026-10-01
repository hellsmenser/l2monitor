using L2Monitor.Infrastructure.Windows;
using L2Monitor.Infrastructure.Windows.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace L2Monitor.Agent.Tests.Windows;

public sealed class LiveWindowsMonitorProbeTests
{
    [Fact]
    public void AssessRemotePorts_ReturnsConnected_WhenGameplayAndSessionPortsArePresent()
    {
        var assessment = LiveWindowsMonitorProbe.AssessRemotePorts([7777, 17453]);

        Assert.Equal("Connected", assessment.State);
        Assert.True(assessment.CountsAsActive);
    }

    [Fact]
    public void AssessRemotePorts_ReturnsGameplayOnly_WhenOnlyGameplayPortRemains()
    {
        var assessment = LiveWindowsMonitorProbe.AssessRemotePorts([7777]);

        Assert.Equal("GameplayOnly", assessment.State);
        Assert.True(assessment.CountsAsActive);
        Assert.NotNull(assessment.Forensics);
        Assert.Equal("gameplay_only", assessment.Forensics!.Kind);
        Assert.Equal(1, assessment.Forensics.EstablishedRowCount);
        Assert.Equal([7777], assessment.Forensics.ObservedRemotePorts);
    }

    [Fact]
    public void AssessRemotePorts_ReturnsConnecting_WhenOnlySessionPortExists()
    {
        var assessment = LiveWindowsMonitorProbe.AssessRemotePorts([17453]);

        Assert.Equal("Connecting", assessment.State);
        Assert.False(assessment.CountsAsActive);
    }

    [Fact]
    public void AssessRemotePorts_ReturnsDisconnected_WhenNoObservedPortsMatch()
    {
        var assessment = LiveWindowsMonitorProbe.AssessRemotePorts([443, 2106]);

        Assert.Equal("Disconnected", assessment.State);
        Assert.False(assessment.CountsAsActive);
    }

    [Fact]
    public void AddL2MonitorWindowsInfrastructure_RegistersLiveProbe()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddL2MonitorWindowsInfrastructure();

        using var serviceProvider = services.BuildServiceProvider();
        var probe = serviceProvider.GetRequiredService<IWindowsMonitorProbe>();

        Assert.IsType<LiveWindowsMonitorProbe>(probe);
    }
}
