using L2Monitor.Tray.Presentation;
using Xunit;

namespace L2Monitor.Tray.Tests.Presentation;

public sealed class TrayStateClassifierTests
{
    [Theory]
    [InlineData("healthy")]
    [InlineData("running")]
    [InlineData("available")]
    [InlineData("ok")]
    [InlineData("configured")]
    public void IsHealthyState_RecognizesCanonicalHealthyStates(string state)
    {
        Assert.True(TrayStateClassifier.IsHealthyState(state));
        Assert.False(TrayStateClassifier.IsWarningState(state));
        Assert.False(TrayStateClassifier.IsErrorState(state));
    }

    [Theory]
    [InlineData("warning")]
    [InlineData("degraded")]
    [InlineData("disabled")]
    [InlineData("idle")]
    public void IsWarningState_RecognizesCanonicalWarningStates(string state)
    {
        Assert.False(TrayStateClassifier.IsHealthyState(state));
        Assert.True(TrayStateClassifier.IsWarningState(state));
        Assert.False(TrayStateClassifier.IsErrorState(state));
    }

    [Theory]
    [InlineData("warning", true)]
    [InlineData("degraded", true)]
    [InlineData("disabled", false)]
    [InlineData("idle", false)]
    public void AffectsTraySeverityAsWarning_OnlyFlagsUserFacingWarningStates(string state, bool expected)
    {
        Assert.Equal(expected, TrayStateClassifier.AffectsTraySeverityAsWarning(state));
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("misconfigured")]
    [InlineData("auth_failed")]
    [InlineData("protocol_error")]
    [InlineData("rate_limited")]
    [InlineData("not_configured")]
    [InlineData("error")]
    [InlineData("unavailable")]
    public void IsErrorState_RecognizesCanonicalErrorStates(string state)
    {
        Assert.False(TrayStateClassifier.IsHealthyState(state));
        Assert.False(TrayStateClassifier.IsWarningState(state));
        Assert.True(TrayStateClassifier.IsErrorState(state));
    }
}
