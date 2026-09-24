namespace AssumptionZero.Domain.InMemory;

public struct WorkingOrders
{
    public WorkingOrder BidOrder;
    public WorkingOrder AskOrder;
}

public struct WorkingOrder
{
    public long OrderId;

    /// <summary>
    /// 65000.00 => 6_500_000 ticks
    /// </summary>
    public long PriceTicks;
    public long Quantity;
    public long FilledQuantity;

    public OrderStatus Status;

    public bool IsActive;
}

public enum OrderStatus
{
    PendingNew,
    New,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected,
    Expired
}