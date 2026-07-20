namespace Halfway.Core;

public enum AlertDeliveryState
{
    Pending = 0,
    Reserved = 1,
    Delivered = 2,
}

public sealed record AlertDelivery(
    Guid EventId,
    Guid ParentSessionId,
    string Message,
    AlertDeliveryState State,
    DateTimeOffset? ReservedAtUtc,
    DateTimeOffset? DeliveredAtUtc,
    DateTimeOffset UpdatedAtUtc);
