namespace AssumptionZero.Domain.InMemory;

public readonly record struct PendingMarkout(long DueNs, long FillPriceTicks, int Side);
