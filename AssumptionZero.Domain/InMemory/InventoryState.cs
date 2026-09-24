namespace AssumptionZero.Domain.InMemory;

public struct InventoryState
{
    public long CurrentQuantity;
    public long TargetQuantity;

    public long ReservedBuyQuantity;
    public long ReservedSellQuantity;

    public long MaxLongQuantity;
    public long MaxShortQuantity;

    public long Deviation
        => CurrentQuantity - TargetQuantity;
}