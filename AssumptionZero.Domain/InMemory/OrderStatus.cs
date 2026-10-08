namespace AssumptionZero.Domain.InMemory;

public enum OrderStatus
{
    PendingNew,
    PendingCancel,
    New,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected,
    Expired
}
