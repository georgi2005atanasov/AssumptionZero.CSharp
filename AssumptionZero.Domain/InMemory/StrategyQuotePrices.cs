namespace AssumptionZero.Domain.InMemory;

/// <summary>Intermediate calculation values carried from price construction to quantity sizing.</summary>
internal readonly record struct StrategyQuotePrices(
    double Direction,
    double Fair,
    double Skew,
    double Reservation,
    double HalfSpread,
    long Bid,
    long Ask,
    double InventoryRatio,
    long LowerInventoryBound,
    double RiskSpread);
