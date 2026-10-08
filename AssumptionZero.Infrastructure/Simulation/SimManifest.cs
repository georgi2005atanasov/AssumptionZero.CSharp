namespace AssumptionZero.Infrastructure.Simulation;

public sealed record SimManifest(
    int Version,
    string Session,
    string Mode,
    int Frames,
    int Warmup,
    string Fee,
    SimInstrument[] Instruments,
    string[] Assets,
    string[] InitialFree,
    string Scenario);
