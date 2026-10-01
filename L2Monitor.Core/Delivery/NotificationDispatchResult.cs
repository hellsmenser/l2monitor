namespace L2Monitor.Core.Delivery;

public sealed record NotificationDispatchResult(
    bool Delivered,
    DeliveryHealthSnapshot Health,
    bool Suppressed = false);
