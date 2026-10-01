namespace L2Monitor.Core.Delivery;

public sealed record DeliveryHealthSnapshot(
    DeliveryMode Mode,
    DeliveryHealthState State,
    string Summary,
    DateTimeOffset? CheckedAtUtc = null,
    DateTimeOffset? LastSuccessAtUtc = null);
