namespace AssumptionZero.Domain.InMemory;

public readonly struct HotConfig
{
    public readonly int BookLevels;
    public readonly int OfiWindow;

    public readonly long TradeFlowWindowNs;
    public readonly long VolatilityWindowNs;
    public readonly long MomentumWindowNs;
    public readonly long LiquidityWindowNs;

    public readonly double MomentumScaleTicks;
    public readonly double VolatilityScaleTicks;
    public readonly double AdverseScaleTicks;

    public readonly double MicroWeight;
    public readonly double MliWeight;
    public readonly double OfiWeight;
    public readonly double TradeFlowWeight;
    public readonly double MomentumWeight;

    public readonly double MaxFairShiftTicks;

    public readonly double MaxInventorySkewTicks;

    public readonly double BaseHalfSpreadTicks;

    public readonly double VolatilitySpreadWeightTicks;
    public readonly double LiquiditySpreadWeightTicks;
    public readonly double AdverseSpreadWeightTicks;
    public readonly double InventorySpreadWeightTicks;

    public readonly long BaseQuantity;

    public readonly double MinRiskSizeFactor;

    public readonly long MinEdgeTicks;
    public readonly long MinimumReplaceMoveTicks;
}