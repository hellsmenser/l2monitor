using L2Monitor.Core.Api;
using L2Monitor.Tray.Updates;
using Xunit;

namespace L2Monitor.Tray.Tests.Updates;

public sealed class UpdateNotificationPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly ClientUpdateDto Available = new(
        "available", "1.0.1", "1.0.2", true, false,
        "https://github.com/hellsmenser/l2monitor/releases/tag/v1.0.2", Now);

    [Fact]
    public void ShouldNotify_IsImmediateThenAtMostOncePerDayForSameRelease()
    {
        Assert.True(UpdateNotificationPolicy.ShouldNotify(Available, null, Now));

        var sent = new UpdateNotificationRecord("1.0.2", Now);
        Assert.False(UpdateNotificationPolicy.ShouldNotify(Available, sent, Now.AddHours(23)));
        Assert.True(UpdateNotificationPolicy.ShouldNotify(Available, sent, Now.AddHours(24)));
    }

    [Fact]
    public void ShouldNotify_AllowsNewerReleaseImmediately()
    {
        var previous = new UpdateNotificationRecord("1.0.1", Now.AddMinutes(-5));

        Assert.True(UpdateNotificationPolicy.ShouldNotify(Available, previous, Now));
    }

    [Fact]
    public void ShouldNotify_DoesNotHideUpdateWhenDownloadUrlIsTemporarilyMissing()
    {
        var update = Available with { ReleaseUrl = null };

        Assert.True(UpdateNotificationPolicy.ShouldNotify(update, null, Now));
    }
}
