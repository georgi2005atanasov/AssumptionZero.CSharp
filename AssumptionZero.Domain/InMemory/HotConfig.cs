namespace AssumptionZero.Domain.InMemory;

public readonly struct HotConfig
{
    public HotConfig() : this(bookLevels: 10) { }

    public HotConfig(int bookLevels = 10, int ofiWindow = 100, long tradeFlowWindowNs = 500_000_000,
        long volatilityWindowNs = 1_000_000_000, long momentumWindowNs = 5_000_000_000,
        long liquidityWindowNs = 1_000_000_000, double momentumScaleTicks = 10,
        double volatilityScaleTicks = 5, double adverseScaleTicks = 5,
        double microWeight = 1, double mliWeight = 1, double ofiWeight = 1,
        double tradeFlowWeight = 1, double momentumWeight = 1, double maxFairShiftTicks = 2,
        double maxInventorySkewTicks = 2, double baseHalfSpreadTicks = 2,
        double volatilitySpreadWeightTicks = 1, double liquiditySpreadWeightTicks = 1,
        double adverseSpreadWeightTicks = 1, double inventorySpreadWeightTicks = 1,
        long baseQuantity = 1_000, double minRiskSizeFactor = 0.25,
        long minEdgeTicks = 1, long minimumReplaceMoveTicks = 2)
    {
        if (bookLevels is < 1 or > 5_000 || ofiWindow is < 1 or > 8_192 ||
            tradeFlowWindowNs <= 0 || volatilityWindowNs <= 0 || momentumWindowNs <= 0 || liquidityWindowNs <= 0 ||
            momentumScaleTicks <= 0 || volatilityScaleTicks <= 0 || adverseScaleTicks <= 0 ||
            baseHalfSpreadTicks <= 0 || baseQuantity <= 0 || minRiskSizeFactor is <= 0 or > 1 ||
            minEdgeTicks < 0 || minimumReplaceMoveTicks < 1 ||
            microWeight < 0 || mliWeight < 0 || ofiWeight < 0 || tradeFlowWeight < 0 || momentumWeight < 0 ||
            microWeight + mliWeight + ofiWeight + tradeFlowWeight + momentumWeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(bookLevels), "Invalid hot configuration.");
        BookLevels = bookLevels;
        OfiWindow = ofiWindow;
        TradeFlowWindowNs = tradeFlowWindowNs;
        VolatilityWindowNs = volatilityWindowNs;
        MomentumWindowNs = momentumWindowNs;
        LiquidityWindowNs = liquidityWindowNs;
        MomentumScaleTicks = momentumScaleTicks;
        VolatilityScaleTicks = volatilityScaleTicks;
        AdverseScaleTicks = adverseScaleTicks;
        MicroWeight = microWeight;
        MliWeight = mliWeight;
        OfiWeight = ofiWeight;
        TradeFlowWeight = tradeFlowWeight;
        MomentumWeight = momentumWeight;
        MaxFairShiftTicks = maxFairShiftTicks;
        MaxInventorySkewTicks = maxInventorySkewTicks;
        BaseHalfSpreadTicks = baseHalfSpreadTicks;
        VolatilitySpreadWeightTicks = volatilitySpreadWeightTicks;
        LiquiditySpreadWeightTicks = liquiditySpreadWeightTicks;
        AdverseSpreadWeightTicks = adverseSpreadWeightTicks;
        InventorySpreadWeightTicks = inventorySpreadWeightTicks;
        BaseQuantity = baseQuantity;
        MinRiskSizeFactor = minRiskSizeFactor;
        MinEdgeTicks = minEdgeTicks;
        MinimumReplaceMoveTicks = minimumReplaceMoveTicks;
    }

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
