namespace L2Monitor.Core.Delivery;

public interface INotificationTransport
{
    DeliveryMode Mode { get; }

    DeliveryHealthSnapshot CurrentHealth { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<NotificationDispatchResult> SendAsync(
        NotificationMessage message,
        CancellationToken cancellationToken = default);
}
