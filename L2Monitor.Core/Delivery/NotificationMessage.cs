using L2Monitor.Core.Models;

namespace L2Monitor.Core.Delivery;

public sealed record NotificationMessage(
    MonitorEvent Event,
    string Text,
    DateTimeOffset OccurredAtUtc,
    IReadOnlyDictionary<string, string?> Metadata);
