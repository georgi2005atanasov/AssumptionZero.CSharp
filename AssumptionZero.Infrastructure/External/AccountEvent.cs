using AssumptionZero.Domain.InMemory;

namespace AssumptionZero.Infrastructure.External;

public readonly record struct AccountEvent(
    AccountEventKind Kind,
    int Asset,
    decimal Free,
    decimal Locked,
    int Runtime,
    bool Buy,
    long OrderId,
    long Filled,
    long FillPriceTicks,
    long TimeNs,
    OrderStatus Status);
