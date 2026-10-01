using System.Drawing;
using L2Monitor.Tray.Presentation;
using Xunit;

namespace L2Monitor.Tray.Tests;

public sealed class TrayApplicationContextTests
{
    [Theory]
    [InlineData((int)TraySeverity.Normal)]
    [InlineData((int)TraySeverity.Info)]
    [InlineData((int)TraySeverity.Warning)]
    [InlineData((int)TraySeverity.Error)]
    public void SelectTrayIcon_AlwaysKeepsProductIcon(int severityValue)
    {
        using var productIcon = (Icon)SystemIcons.Application.Clone();
        var severity = (TraySeverity)severityValue;

        var selectedIcon = TrayApplicationContext.SelectTrayIcon(productIcon, severity);

        Assert.Same(productIcon, selectedIcon);
    }
}
