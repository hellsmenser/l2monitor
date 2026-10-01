using L2Monitor.Agent.Hosting;
using L2Monitor.Core.Models;
using L2Monitor.Infrastructure.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace L2Monitor.Agent.Runtime;

internal sealed class AgentRuntimeService : BackgroundService
{
    private static readonly TimeSpan DeadStartedNotificationCooldown = TimeSpan.FromMinutes(1);

    private readonly AgentLaunchOptions _launchOptions;
    private readonly TimeProvider _timeProvider;
    private readonly AgentRuntimeStateStore _stateStore;
    private readonly AgentLifecycleMarkerStore _lifecycleMarkerStore;
    private readonly AgentControlStateStore _controlState;
    private readonly AgentConfigurationService _configuration;
    private readonly IAgentRuntimeProbe _probe;
    private readonly IAudioDeathDetector _audioDeathDetector;
    private readonly IAgentNotificationSender _notificationSender;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<AgentRuntimeService> _logger;
    private readonly TimeSpan _minimumPollInterval;
    private readonly Dictionary<int, DateTimeOffset> _lastPublishedDeadStartedByPid = [];

    public AgentRuntimeService(
        AgentLaunchOptions launchOptions,
        TimeProvider timeProvider,
        AgentRuntimeStateStore stateStore,
        AgentLifecycleMarkerStore lifecycleMarkerStore,
        AgentControlStateStore controlState,
        AgentConfigurationService configuration,
        IAgentRuntimeProbe probe,
        IAudioDeathDetector audioDeathDetector,
        IAgentNotificationSender notificationSender,
        IHostApplicationLifetime applicationLifetime,
        AgentRuntimeOptions options,
        ILogger<AgentRuntimeService> logger)
    {
        _launchOptions = launchOptions;
        _timeProvider = timeProvider;
        _stateStore = stateStore;
        _lifecycleMarkerStore = lifecycleMarkerStore;
        _controlState = controlState;
        _configuration = configuration;
        _probe = probe;
        _audioDeathDetector = audioDeathDetector;
        _notificationSender = notificationSender;
        _applicationLifetime = applicationLifetime;
        _logger = logger;

        _minimumPollInterval = options.PollInterval < TimeSpan.FromMilliseconds(250)
            ? TimeSpan.FromMilliseconds(250)
            : options.PollInterval;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var recoveryRecord = _lifecycleMarkerStore.InitializeStartup(now);
        _stateStore.Update(new AgentRuntimeState(
            Status: AgentRuntimeStatus.Starting,
            StartedAtUtc: now,
            LastProbeCompletedAtUtc: null,
            LastError: null,
            ProbeCount: 0,
            LastObservedProcessCount: 0,
            LastObservedConnectionCount: 0,
            Connections: []));

        _logger.LogInformation(
            "Agent runtime starting. PollIntervalMs={PollIntervalMs} RunOnce={RunOnce}",
            _configuration.GetSnapshot().Settings.Monitor.PollIntervalMs,
            _launchOptions.RunOnce);
        _controlState.RecordIncident("agent_started", "info", "Agent runtime started.", now);
        if (recoveryRecord is not null)
        {
            _controlState.RecordIncident(recoveryRecord.Kind, recoveryRecord.Severity, recoveryRecord.Summary, now);
            _logger.LogWarning("Agent startup recovery detected: {Summary}", recoveryRecord.Summary);
        }

        PublishConfigurationDiagnostics("startup", now);

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await ProbeAsync(stoppingToken).ConfigureAwait(false);

            if (_launchOptions.RunOnce)
            {
                _logger.LogInformation("Run-once startup probe completed. Stopping host.");
                _applicationLifetime.StopApplication();
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = ResolvePollInterval();
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                await ProbeAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Agent runtime cancellation requested.");
        }
        catch (Exception ex)
        {
            var faultedAt = _timeProvider.GetUtcNow();
            var snapshot = _stateStore.GetSnapshot();
            _stateStore.Update(snapshot with
            {
                Status = AgentRuntimeStatus.Faulted,
                LastError = ex.Message,
            });
            _controlState.RecordIncident("agent_faulted", "error", ex.Message, faultedAt);
            _logger.LogError(ex, "Agent runtime terminated unexpectedly.");
            TryRecordLifecycleStatus(AgentRuntimeStatus.Faulted, faultedAt, "fault");
            throw;
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        var snapshot = _stateStore.GetSnapshot();
        var stoppedAt = _timeProvider.GetUtcNow();
        _stateStore.Update(snapshot with
        {
            Status = snapshot.Status == AgentRuntimeStatus.Faulted
                ? AgentRuntimeStatus.Faulted
                : AgentRuntimeStatus.Stopped,
        });

        _logger.LogInformation(
            "Agent runtime stopping. Status={Status} ProbeCount={ProbeCount}",
            _stateStore.GetSnapshot().Status,
            _stateStore.GetSnapshot().ProbeCount);
        TryRecordLifecycleStatus(_stateStore.GetSnapshot().Status, stoppedAt, "stop");
        _controlState.RecordIncident("agent_stopped", "info", "Agent runtime stopped.", stoppedAt);

        return base.StopAsync(cancellationToken);
    }

    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        var probeResult = await _probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = _stateStore.GetSnapshot();
        var configuration = _configuration.GetSnapshot();

        var observedConnections = probeResult.Connections
            .Select(connection => new AgentConnectionRecord(
                connection.Id,
                connection.ProcessName,
                connection.WindowTitle,
                connection.State,
                connection.ObservedAtUtc,
                connection.Forensics))
            .ToArray();

        var mergedConnections = MergeObservedConnections(snapshot.Connections, observedConnections);

        await PublishRuntimeNotificationsAsync(
                snapshot.Connections,
                mergedConnections,
                configuration.Settings.Monitor.IgnoredWindowTitlesText,
                configuration.Settings.Monitor.MinConfirmLifetimeSec,
                snapshot.LastProbeCompletedAtUtc,
                probeResult.CapturedAtUtc,
                cancellationToken)
            .ConfigureAwait(false);

        _audioDeathDetector.UpdateTrackedProcessIds(
            mergedConnections
                .Select(static connection => ParsePid(connection.Id))
                .Where(static pid => pid > 0)
                .Distinct()
                .ToArray());

        await PublishAudioNotificationsAsync(mergedConnections, probeResult.CapturedAtUtc, cancellationToken)
            .ConfigureAwait(false);

        _stateStore.Update(snapshot with
        {
            Status = AgentRuntimeStatus.Running,
            LastProbeCompletedAtUtc = probeResult.CapturedAtUtc,
            LastError = null,
            ProbeCount = snapshot.ProbeCount + 1,
            LastObservedProcessCount = probeResult.CandidateProcessCount,
            LastObservedConnectionCount = probeResult.GameConnectionCount,
            Connections = mergedConnections,
        });
        TryRecordLifecycleStatus(AgentRuntimeStatus.Running, probeResult.CapturedAtUtc, "probe");

        _logger.LogInformation(
            "Agent probe completed. CandidateProcesses={CandidateProcesses} GameConnections={GameConnections} ProbeCount={ProbeCount}",
            probeResult.CandidateProcessCount,
            probeResult.GameConnectionCount,
            snapshot.ProbeCount + 1);
    }

    private async Task PublishRuntimeNotificationsAsync(
        IReadOnlyList<AgentConnectionRecord> previousConnections,
        IReadOnlyList<AgentConnectionRecord> currentConnections,
        string? ignoredWindowTitlesText,
        int minConfirmLifetimeSec,
        DateTimeOffset? previousProbeCompletedAtUtc,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        var previousById = previousConnections.ToDictionary(static connection => connection.Id, StringComparer.OrdinalIgnoreCase);
        var currentById = currentConnections.ToDictionary(static connection => connection.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var previous in previousConnections)
        {
            if (currentById.ContainsKey(previous.Id))
            {
                continue;
            }

            if (ShouldIgnoreConnection(previous, ignoredWindowTitlesText))
            {
                continue;
            }

            if (!CanEmitDisconnectEvent(previous))
            {
                continue;
            }

            if (!HasMetDisconnectConfirmLifetime(previousProbeCompletedAtUtc, observedAtUtc, minConfirmLifetimeSec))
            {
                continue;
            }

            var pid = ParsePid(previous.Id);
            var processStillExists = IsProcessAlive(pid);
            var kind = processStillExists ? "client_disconnected" : "process_exited";
            var severity = processStillExists ? "warning" : "info";
            var summary = processStillExists
                ? $"Client {previous.ProcessName} ({previous.Id}) lost active game connections."
                : $"Client {previous.ProcessName} ({previous.Id}) exited.";
            var monitorEvent = new MonitorEvent(
                observedAtUtc.UtcDateTime,
                pid,
                previous.ProcessName,
                processStillExists ? MonitorEventKind.ClientDisconnected : MonitorEventKind.ProcessExited,
                0);

            await RecordAndSendMonitorEventAsync(kind, severity, summary, monitorEvent, previous, observedAtUtc, null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RecordAndSendMonitorEventAsync(
        string kind,
        string severity,
        string summary,
        MonitorEvent monitorEvent,
        AgentConnectionRecord connection,
        DateTimeOffset occurredAtUtc,
        IReadOnlyDictionary<string, string?>? additionalMetadata,
        CancellationToken cancellationToken)
    {
        _controlState.RecordIncident(kind, severity, summary, occurredAtUtc);

        var configuration = _configuration.GetSnapshot();
        var metadata = new Dictionary<string, string?>
        {
            ["connectionId"] = connection.Id,
            ["processName"] = connection.ProcessName,
            ["windowTitle"] = connection.WindowTitle,
            ["state"] = connection.State,
        };
        if (additionalMetadata is not null)
        {
            foreach (var pair in additionalMetadata)
            {
                metadata[pair.Key] = pair.Value;
            }
        }

        var dispatch = await _notificationSender.SendMonitorEventAsync(configuration, monitorEvent, metadata, cancellationToken)
            .ConfigureAwait(false);

        _controlState.SetLastNotificationResult(new AgentActionResultRecord(
            "runtime-notification",
            dispatch.Suppressed ? "skipped" : dispatch.Delivered ? "completed" : "failed",
            dispatch.Health.Summary,
            dispatch.Health.CheckedAtUtc ?? occurredAtUtc));

        if (!dispatch.Suppressed)
        {
            _controlState.SetLastDeliveryHealth(dispatch.Health);
        }
    }

    private async Task PublishAudioNotificationsAsync(
        IReadOnlyList<AgentConnectionRecord> currentConnections,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        var detections = _audioDeathDetector.DrainDetections(observedAtUtc);
        if (detections.Count == 0)
        {
            return;
        }

        foreach (var detection in detections
                     .GroupBy(static item => item.ProcessId)
                     .Select(static group => group
                         .OrderByDescending(item => item.Confidence)
                         .ThenByDescending(item => item.DetectedAtUtc)
                         .First()))
        {
            if (_lastPublishedDeadStartedByPid.TryGetValue(detection.ProcessId, out var lastPublishedAtUtc)
                && detection.DetectedAtUtc - lastPublishedAtUtc < DeadStartedNotificationCooldown)
            {
                _logger.LogInformation(
                    "Suppressing duplicate death-audio notification. ProcessId={ProcessId} Confidence={Confidence:F3}",
                    detection.ProcessId,
                    detection.Confidence);
                continue;
            }

            _lastPublishedDeadStartedByPid[detection.ProcessId] = detection.DetectedAtUtc;
            var connection = SelectAudioEventConnection(currentConnections, detection.ProcessId, detection.DetectedAtUtc);
            var summary = connection.Id == "game-audio"
                ? "Detected character death audio in the captured game stream."
                : $"Detected character death audio for {connection.ProcessName} ({connection.Id}).";

            await RecordAndSendMonitorEventAsync(
                    "dead_started",
                    "info",
                    summary,
                    new MonitorEvent(detection.DetectedAtUtc.UtcDateTime, ParsePid(connection.Id), connection.ProcessName, MonitorEventKind.DeadStarted, 0),
                    connection,
                    detection.DetectedAtUtc,
                    new Dictionary<string, string?>
                    {
                        ["audioReference"] = detection.ReferenceName,
                        ["audioConfidence"] = detection.Confidence.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                        ["audioProcessId"] = detection.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["audioBatchSize"] = detections.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static AgentConnectionRecord[] MergeObservedConnections(
        IReadOnlyList<AgentConnectionRecord> previousConnections,
        IReadOnlyList<AgentConnectionRecord> currentConnections)
    {
        var previousById = previousConnections.ToDictionary(static connection => connection.Id, StringComparer.OrdinalIgnoreCase);

        return currentConnections
            .Select(current => previousById.TryGetValue(current.Id, out var previous)
                ? string.Equals(previous.State, current.State, StringComparison.Ordinal)
                    ? current with { ObservedAtUtc = previous.ObservedAtUtc }
                    : current
                : current)
            .ToArray();
    }

    private static bool HasMetConfirmLifetime(
        AgentConnectionRecord connection,
        DateTimeOffset observedAtUtc,
        int minConfirmLifetimeSec) =>
        observedAtUtc - connection.ObservedAtUtc >= TimeSpan.FromSeconds(minConfirmLifetimeSec);

    private static bool HasMetDisconnectConfirmLifetime(
        DateTimeOffset? previousProbeCompletedAtUtc,
        DateTimeOffset observedAtUtc,
        int minConfirmLifetimeSec) =>
        previousProbeCompletedAtUtc.HasValue
        && observedAtUtc - previousProbeCompletedAtUtc.Value >= TimeSpan.FromSeconds(minConfirmLifetimeSec);

    private static bool CanEmitDisconnectEvent(AgentConnectionRecord connection) =>
        string.Equals(connection.State, "Connected", StringComparison.Ordinal)
        || string.Equals(connection.State, "GameplayOnly", StringComparison.Ordinal);

    private static AgentConnectionRecord SelectAudioEventConnection(
        IReadOnlyList<AgentConnectionRecord> currentConnections,
        int processId,
        DateTimeOffset observedAtUtc)
    {
        var exact = currentConnections.FirstOrDefault(connection => ParsePid(connection.Id) == processId);
        if (exact is not null)
        {
            return exact;
        }

        var connected = currentConnections
            .Where(static connection => string.Equals(connection.State, "Connected", StringComparison.Ordinal))
            .ToArray();
        if (connected.Length == 1)
        {
            return connected[0];
        }

        var suspected = currentConnections
            .Where(static connection => string.Equals(connection.State, "GameplayOnly", StringComparison.Ordinal)
                || string.Equals(connection.State, "SuspectedGhostDisconnect", StringComparison.Ordinal))
            .ToArray();
        if (suspected.Length == 1)
        {
            return suspected[0];
        }

        var fallback = currentConnections.Count == 1
            ? currentConnections[0]
            : currentConnections
                .OrderByDescending(static connection => connection.ObservedAtUtc)
                .FirstOrDefault();

        return fallback
            ?? new AgentConnectionRecord("game-audio", "game-audio", null, "AudioOnly", observedAtUtc);
    }

    private static int ParsePid(string connectionId)
    {
        const string prefix = "pid-";
        return connectionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(connectionId[prefix.Length..], out var pid)
                ? pid
                : 0;
    }

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool ShouldIgnoreConnection(AgentConnectionRecord connection, string? ignoredWindowTitlesText)
    {
        if (string.IsNullOrWhiteSpace(ignoredWindowTitlesText) || string.IsNullOrWhiteSpace(connection.WindowTitle))
        {
            return false;
        }

        var filters = ignoredWindowTitlesText
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var filter in filters)
        {
            if (connection.WindowTitle.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void TryRecordLifecycleStatus(AgentRuntimeStatus status, DateTimeOffset occurredAtUtc, string phase)
    {
        try
        {
            _lifecycleMarkerStore.RecordStatus(status, occurredAtUtc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to persist runtime-state.json during agent {Phase}.", phase);
        }
    }

    private TimeSpan ResolvePollInterval()
    {
        var configured = TimeSpan.FromMilliseconds(_configuration.GetSnapshot().Settings.Monitor.PollIntervalMs);
        return configured < _minimumPollInterval ? _minimumPollInterval : configured;
    }

    private void PublishConfigurationDiagnostics(string source, DateTimeOffset occurredAtUtc)
    {
        foreach (var diagnostic in _configuration.GetDiagnostics())
        {
            var logLevel = string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase)
                ? LogLevel.Error
                : LogLevel.Warning;

            _logger.Log(
                logLevel,
                "Agent {Source} diagnostic {Kind}: {Summary}",
                source,
                diagnostic.Kind,
                diagnostic.Summary);
            _controlState.RecordIncident(diagnostic.Kind, diagnostic.Severity, diagnostic.Summary, occurredAtUtc);
        }
    }
}
