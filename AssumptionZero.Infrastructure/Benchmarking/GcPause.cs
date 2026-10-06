namespace AssumptionZero.Infrastructure.Benchmarking;

public sealed record GcPause(long StartUtcTicks, long EndUtcTicks, int Reason);