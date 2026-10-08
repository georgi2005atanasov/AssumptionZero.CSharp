namespace AssumptionZero.Infrastructure.Simulation;

public sealed record ExchangePacket(
    string Kind,
    long Step = 0,
    OrderCommand[]? Commands = null,
    ExecutionEvent[]? Events = null,
    SimManifest? Manifest = null);
