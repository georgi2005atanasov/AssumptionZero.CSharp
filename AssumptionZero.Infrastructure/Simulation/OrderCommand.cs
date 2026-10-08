namespace AssumptionZero.Infrastructure.Simulation;

public sealed record OrderCommand(
    long Id,
    long Intent,
    int Instrument,
    bool Buy,
    string Action,
    long OrderId,
    long Price,
    long Quantity,
    long EventTime);
