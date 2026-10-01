using L2Monitor.Tray.Bootstrap;
using Xunit;

namespace L2Monitor.Tray.Tests.Bootstrap;

public sealed class AutostartServiceTests
{
    [Fact]
    public void BuildRunCommand_QuotesExecutableAndStartsInBackground()
    {
        var executablePath = Path.Combine("C:\\Program Files", "Aden+", "Aden+.exe");

        var command = WindowsAutostartService.BuildRunCommand(executablePath);

        Assert.Equal($"\"{Path.GetFullPath(executablePath)}\" --background", command);
    }

    [Theory]
    [InlineData("  \"C:\\Apps\\Aden+.exe\" --background  ", "\"C:\\Apps\\Aden+.exe\" --background", true)]
    [InlineData("\"C:\\Apps\\Old.exe\" --background", "\"C:\\Apps\\Aden+.exe\" --background", false)]
    [InlineData(null, "\"C:\\Apps\\Aden+.exe\" --background", false)]
    public void CommandsMatch_RequiresCurrentExecutable(string? actual, string expected, bool result)
    {
        Assert.Equal(result, WindowsAutostartService.CommandsMatch(actual, expected));
    }
}
