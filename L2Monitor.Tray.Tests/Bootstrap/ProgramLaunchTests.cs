namespace L2Monitor.Tray.Tests.Bootstrap;

using Xunit;

public sealed class ProgramLaunchTests
{
    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--background" }, true)]
    [InlineData(new[] { "--BACKGROUND" }, true)]
    public void ShouldStartMinimized_RecognizesAutostartArgument(string[] args, bool expected)
    {
        Assert.Equal(expected, Program.ShouldStartMinimized(args));
    }
}
