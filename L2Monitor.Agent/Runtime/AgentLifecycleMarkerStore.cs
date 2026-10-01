using System.Text.Json;
using L2Monitor.Agent.Hosting;

namespace L2Monitor.Agent.Runtime;

internal sealed class AgentLifecycleMarkerStore
{
    private const string MarkerFileName = "runtime-state.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _markerPath;

    public AgentLifecycleMarkerStore()
    {
        _markerPath = Path.Combine(AgentConfigurationService.ResolveRootDirectory(), MarkerFileName);
    }

    public AgentLifecycleRecoveryRecord? InitializeStartup(DateTimeOffset occurredAtUtc)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);

        var previous = TryRead();
        Write(new AgentLifecycleMarker(AgentRuntimeStatus.Starting, occurredAtUtc));

        return BuildRecoveryRecord(previous);
    }

    public void RecordStatus(AgentRuntimeStatus status, DateTimeOffset occurredAtUtc) =>
        Write(new AgentLifecycleMarker(status, occurredAtUtc));

    private static AgentLifecycleRecoveryRecord? BuildRecoveryRecord(AgentLifecycleMarkerReadResult previous)
    {
        if (previous.LoadFailureSummary is not null)
        {
            return new AgentLifecycleRecoveryRecord(
                "agent_recovered_with_unreadable_runtime_state",
                "warning",
                $"Previous agent runtime-state.json could not be read; last shutdown state is unknown. {previous.LoadFailureSummary}");
        }

        return previous.Marker is null
            ? null
            : BuildRecoveryRecord(previous.Marker);
    }

    private static AgentLifecycleRecoveryRecord? BuildRecoveryRecord(AgentLifecycleMarker previous) =>
        previous.Status switch
        {
            AgentRuntimeStatus.Starting or AgentRuntimeStatus.Running => new AgentLifecycleRecoveryRecord(
                "agent_recovered_after_unclean_shutdown",
                "warning",
                $"Previous agent run ended without a clean stop (last state: {previous.Status} at {previous.UpdatedAtUtc:O})."),
            AgentRuntimeStatus.Faulted => new AgentLifecycleRecoveryRecord(
                "agent_recovered_after_fault",
                "warning",
                $"Previous agent run faulted before restart (recorded at {previous.UpdatedAtUtc:O})."),
            _ => null,
        };

    private AgentLifecycleMarkerReadResult TryRead()
    {
        if (!File.Exists(_markerPath))
        {
            return AgentLifecycleMarkerReadResult.Missing;
        }

        try
        {
            var json = File.ReadAllText(_markerPath);
            var marker = JsonSerializer.Deserialize<AgentLifecycleMarker>(json, JsonOptions);
            return marker is null
                ? AgentLifecycleMarkerReadResult.FromFailure("The file did not contain a valid lifecycle marker payload.")
                : AgentLifecycleMarkerReadResult.FromMarker(marker);
        }
        catch (Exception ex)
        {
            return AgentLifecycleMarkerReadResult.FromFailure(ex.Message);
        }
    }

    private void Write(AgentLifecycleMarker marker)
    {
        var directory = Path.GetDirectoryName(_markerPath)
            ?? throw new InvalidOperationException("Lifecycle marker path must have a parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $"{Path.GetFileName(_markerPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, marker, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_markerPath))
            {
                File.Replace(tempPath, _markerPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _markerPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}

internal sealed record AgentLifecycleMarkerReadResult(
    AgentLifecycleMarker? Marker,
    string? LoadFailureSummary)
{
    public static AgentLifecycleMarkerReadResult Missing { get; } = new(null, null);

    public static AgentLifecycleMarkerReadResult FromMarker(AgentLifecycleMarker marker) =>
        new(marker, null);

    public static AgentLifecycleMarkerReadResult FromFailure(string summary) =>
        new(null, summary);
}

internal sealed record AgentLifecycleRecoveryRecord(
    string Kind,
    string Severity,
    string Summary);

internal sealed record AgentLifecycleMarker(
    AgentRuntimeStatus Status,
    DateTimeOffset UpdatedAtUtc);
