namespace L2Monitor.Core.Delivery;

public enum DeliveryHealthState
{
    Disabled,
    Healthy,
    NotConfigured,
    Misconfigured,
    AuthFailed,
    Unreachable,
    ProtocolError,
    RateLimited,
}
