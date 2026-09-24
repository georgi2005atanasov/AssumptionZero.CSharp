namespace AssumptionZero.Domain.InMemory;

public struct PricingState
{
    public double DirectionScore;

    public double FairTicks;
    public double InventorySkewTicks;
    public double ReservationTicks;

    public double HalfSpreadTicks;

    public long TargetBidTicks;
    public long TargetAskTicks;

    public long TargetBidQuantity;
    public long TargetAskQuantity;
}