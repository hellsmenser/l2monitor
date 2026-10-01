using L2Monitor.Core.Delivery;
using L2Monitor.Infrastructure.Windows;

namespace L2Monitor.Agent.Runtime;

internal sealed class AgentControlStateStore
{
    private const int MaxIncidents = 200;
    private readonly object _sync = new();
    private List<AgentIncidentRecord> _incidents = [];
    private AgentActionResultRecord? _lastNotificationResult;
    private DeliveryHealthSnapshot? _lastDeliveryHealth;
    private AgentComponentHealthRecord? _lastBackendHealth;

    public void RecordIncident(string kind, string severity, string summary, DateTimeOffset occurredAtUtc)
    {
        lock (_sync)
        {
            _incidents.Insert(0, new AgentIncidentRecord(Guid.NewGuid().ToString("N"), kind, severity, summary, occurredAtUtc));
            if (_incidents.Count > MaxIncidents)
            {
                _incidents.RemoveRange(MaxIncidents, _incidents.Count - MaxIncidents);
            }
        }
    }

    public IReadOnlyList<AgentIncidentRecord> GetIncidents(int limit)
    {
        lock (_sync)
        {
            return [.. _incidents.Take(Math.Clamp(limit, 1, MaxIncidents))];
        }
    }

    public AgentIncidentRecord? GetLastIncident()
    {
        lock (_sync)
        {
            return _incidents.FirstOrDefault();
        }
    }

    public void SetLastNotificationResult(AgentActionResultRecord result)
    {
        lock (_sync)
        {
            _lastNotificationResult = result;
        }
    }

    public AgentActionResultRecord? GetLastNotificationResult()
    {
        lock (_sync)
        {
            return _lastNotificationResult;
        }
    }

    public void SetLastDeliveryHealth(DeliveryHealthSnapshot health)
    {
        lock (_sync)
        {
            _lastDeliveryHealth = health;
        }
    }

    public DeliveryHealthSnapshot? GetLastDeliveryHealth()
    {
        lock (_sync)
        {
            return _lastDeliveryHealth;
        }
    }

    public void SetLastBackendHealth(AgentComponentHealthRecord health)
    {
        lock (_sync)
        {
            _lastBackendHealth = health;
        }
    }

    public AgentComponentHealthRecord? GetLastBackendHealth()
    {
        lock (_sync)
        {
            return _lastBackendHealth;
        }
    }

    public void InvalidateCachedHealth()
    {
        lock (_sync)
        {
            _lastDeliveryHealth = null;
            _lastBackendHealth = null;
        }
    }
}

internal sealed record AgentIncidentRecord(
    string Id,
    string Kind,
    string Severity,
    string Summary,
    DateTimeOffset OccurredAtUtc);

internal sealed record AgentConnectionRecord(
    string Id,
    string ProcessName,
    string? WindowTitle,
    string State,
    DateTimeOffset ObservedAtUtc,
    AgentConnectionForensics? Forensics = null);

internal sealed record AgentActionResultRecord(
    string Action,
    string Outcome,
    string Message,
    DateTimeOffset OccurredAtUtc);

internal sealed record AgentComponentHealthRecord(
    string State,
    string Summary,
    DateTimeOffset? CheckedAtUtc = null,
    DateTimeOffset? LastSuccessAtUtc = null);
