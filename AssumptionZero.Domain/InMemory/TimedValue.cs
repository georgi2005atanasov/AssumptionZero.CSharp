namespace AssumptionZero.Domain.InMemory;

public readonly record struct TimedValue(long TimeNs, double Value);
