using System.Reflection;
using L2Monitor.Agent.Hosting;
using L2Monitor.Agent.Runtime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace L2Monitor.Agent.Updates;

internal sealed class AgentReleaseUpdateService(
    IAgentUpdateEndpointProvider endpointProvider,
    IAgentReleaseUpdateChecker checker,
    AgentControlStateStore controlState,
    TimeProvider timeProvider,
    ILogger<AgentReleaseUpdateService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private readonly IAgentUpdateEndpointProvider _endpointProvider = endpointProvider;
    private readonly IAgentReleaseUpdateChecker _checker = checker;
    private readonly AgentControlStateStore _controlState = controlState;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<AgentReleaseUpdateService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var currentVersion = ResolveCurrentVersion();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var backendBaseUri = _endpointProvider.GetBackendBaseUri();
                Store(backendBaseUri is null
                    ? new AgentUpdateCheckResult(
                        "unconfigured", currentVersion, null, false, false, null, _timeProvider.GetUtcNow())
                    : await _checker.CheckAsync(backendBaseUri, currentVersion, stoppingToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "Unexpected update-check failure. ExceptionType={ExceptionType}",
                    ex.GetType().Name);
            }

            try
            {
                await Task.Delay(CheckInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal static string ResolveCurrentVersion() =>
        BackendReleaseUpdateChecker.NormalizeVersionForDisplay(
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString())
        ?? "0.0.0";

    private void Store(AgentUpdateCheckResult result)
    {
        var previous = _controlState.GetLastUpdate();
        var preserveKnownUpdate = result.State == "unreachable" && previous?.IsUpdateAvailable == true;
        _controlState.SetLastUpdate(new AgentUpdateStateRecord(
            result.State,
            result.CurrentVersion,
            preserveKnownUpdate ? previous!.LatestVersion : result.LatestVersion,
            preserveKnownUpdate || result.IsUpdateAvailable,
            preserveKnownUpdate ? previous!.Required : result.Required,
            preserveKnownUpdate ? previous!.ReleaseUrl : result.ReleaseUrl,
            result.CheckedAtUtc));
    }
}

internal interface IAgentUpdateEndpointProvider
{
    Uri? GetBackendBaseUri();
}

internal sealed class AgentUpdateEndpointProvider(
    AgentUpdateEndpointConfiguration packagedConfiguration,
    AgentConfigurationService configuration) : IAgentUpdateEndpointProvider
{
    private readonly AgentUpdateEndpointConfiguration _packagedConfiguration = packagedConfiguration;
    private readonly AgentConfigurationService _configuration = configuration;

    public Uri? GetBackendBaseUri() => ResolveBackendBaseUri(
        _packagedConfiguration.BackendBaseUrl,
        _configuration.GetSnapshot().Settings.Cloud.BackendBaseUrl);

    internal static Uri? ResolveBackendBaseUri(string? packagedValue, string? persistedValue)
    {
        foreach (var value in new[] { packagedValue, persistedValue })
        {
            if (AgentBackendUriPolicy.TryResolve(value, out var candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

internal sealed record AgentUpdateEndpointConfiguration(string? BackendBaseUrl);
