using System.Net;
using System.Text;
using L2Monitor.Agent.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace L2Monitor.Agent.Tests.Updates;

public sealed class BackendReleaseUpdateCheckerTests
{
    [Fact]
    public async Task CheckAsync_UsesExistingPublicBackendContract()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"version\":\"1.0.2\",\"required\":true,\"download_url\":\"https://github.com/hellsmenser/l2monitor/releases/tag/v1.0.2\"}",
                Encoding.UTF8,
                "application/json"),
        });
        var checker = new BackendReleaseUpdateChecker(
            new StubHttpClientFactory(handler),
            TimeProvider.System,
            NullLogger<BackendReleaseUpdateChecker>.Instance);

        var result = await checker.CheckAsync(
            new Uri("https://service.example/api/"),
            currentVersion: "1.0.1",
            CancellationToken.None);

        Assert.Equal("available", result.State);
        Assert.True(result.IsUpdateAvailable);
        Assert.True(result.Required);
        Assert.Equal("1.0.2", result.LatestVersion);
        Assert.Equal("https://github.com/hellsmenser/l2monitor/releases/tag/v1.0.2", result.ReleaseUrl);
        Assert.Equal("https://service.example/public/client-version", handler.RequestUri?.AbsoluteUri);
        Assert.Null(handler.Authorization);
    }

    [Fact]
    public async Task CheckAsync_RejectsNonHttpsDownloadUrl()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"version\":\"9.0.0\",\"required\":false,\"download_url\":\"http://attacker.example/update.exe\"}",
                Encoding.UTF8,
                "application/json"),
        });
        var checker = new BackendReleaseUpdateChecker(
            new StubHttpClientFactory(handler),
            TimeProvider.System,
            NullLogger<BackendReleaseUpdateChecker>.Instance);

        var result = await checker.CheckAsync(
            new Uri("https://service.example/"),
            currentVersion: "1.0.1",
            CancellationToken.None);

        Assert.Equal("invalid_response", result.State);
        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.ReleaseUrl);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    public async Task CheckAsync_StillReportsUpdateWhenBackendHasNotPublishedDownloadUrl(string downloadUrlJson)
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"version\":\"1.0.2\",\"required\":false,\"download_url\":{downloadUrlJson}}}",
                Encoding.UTF8,
                "application/json"),
        });
        var checker = new BackendReleaseUpdateChecker(
            new StubHttpClientFactory(handler),
            TimeProvider.System,
            NullLogger<BackendReleaseUpdateChecker>.Instance);

        var result = await checker.CheckAsync(
            new Uri("https://service.example/"),
            currentVersion: "1.0.1",
            CancellationToken.None);

        Assert.Equal("available", result.State);
        Assert.True(result.IsUpdateAvailable);
        Assert.Null(result.ReleaseUrl);
    }

    [Theory]
    [InlineData("1.0.2", "1.0.1", true)]
    [InlineData("v1.0.1", "1.0.1.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    public void IsNewerStableVersion_UsesNumericReleaseOrdering(string latest, string current, bool expected)
    {
        Assert.Equal(expected, BackendReleaseUpdateChecker.IsNewerStableVersion(latest, current));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(response);
        }
    }
}
