using L2Monitor.Tray.Api;
using L2Monitor.Tray.Presentation;
using Xunit;

namespace L2Monitor.Tray.Tests.Presentation;

public sealed class TrayLabelCanonTests
{
    [Fact]
    public void UnknownSummaries_DoNotExposeRawDetails()
    {
        const string sensitiveDetail = "socket path C:\\Users\\secret backend transport detail";

        Assert.DoesNotContain(
            sensitiveDetail,
            TrayLabelCanon.DescribeHealthSummary("protocol_error", sensitiveDetail),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            sensitiveDetail,
            TrayLabelCanon.DescribeIncidentSummary("agent_faulted", sensitiveDetail),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            sensitiveDetail,
            TrayLabelCanon.DescribeIncidentSummary("unexpected_internal_event", sensitiveDetail),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("process_exited", "L2Monitor: client pid malformed closed.")]
    [InlineData("process_exited", "L2Monitor: client pid secret (name) closed.")]
    [InlineData("timeout_exceeded", "L2Monitor: client pid malformed) timed out after 15s.")]
    [InlineData("timeout_exceeded", "L2Monitor: client pid 321 (name) timed out after secrets.")]
    [InlineData("process_exited", "Client malformed exited.")]
    [InlineData("process_exited", "Client malformed () exited.")]
    public void MalformedRecognizedSummaries_DoNotLeakInput(string kind, string malformedSummary)
    {
        var text = TrayLabelCanon.DescribeIncidentSummary(kind, malformedSummary);

        Assert.DoesNotContain("malformed", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeFailure_DoesNotExposeRawTransportOrExceptionDetails()
    {
        var api = TrayLabelCanon.DescribeFailure(new LocalControlApiException(
            System.Net.HttpStatusCode.InternalServerError,
            "request_failed",
            "HTTP WebSocket backend secret detail"));
        var connection = TrayLabelCanon.DescribeFailure(new AgentConnectionException("socket path C:\\secret"));

        Assert.DoesNotContain("HTTP", api, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WebSocket", api, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", api, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("socket", connection, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", connection, StringComparison.OrdinalIgnoreCase);
    }
}
