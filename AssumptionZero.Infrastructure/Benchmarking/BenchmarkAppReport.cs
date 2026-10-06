using System.Text.Json;

namespace AssumptionZero.Infrastructure.Benchmarking;

public sealed record BenchmarkAppReport(
    int SchemaVersion,
    bool Completed,
    long ClockFrequency,
    int WarmupFrames,
    int ReceivedFrames,
    int TotalDecisions,
    string DecisionHash,
    int MeasuredFrames,
    int MeasuredDecisions,
    long MeasuredBytes,
    long MeasuredDurationNanoseconds,
    LatencySummary ProcessingNanoseconds,
    LatencySummary ReceiveToDecisionNanoseconds,
    LatencySummary DecodeNanoseconds,
    LatencySummary BookNanoseconds,
    LatencySummary SignalNanoseconds,
    LatencySummary QuoteNanoseconds,
    long CpuNanoseconds,
    double CpuCores,
    double CpuNanosecondsPerFrame,
    long ResidentBytes,
    long PeakResidentBytes,
    long? ManagedHeapBytes,
    long? ManagedAllocatedBytes,
    double? ManagedAllocatedBytesPerFrame,
    int[]? GcCollections,
    long? GcPauseNanoseconds,
    LatencySummary IndividualGcPauseNanoseconds,
    bool Diagnostic,
    GcPause[] GcPauses,
    FrameSample[] Frames)
{
    public string BuildConfiguration { get; init; } =
#if DEBUG
        "Debug";
#else
        "Release";
#endif
    public bool? ServerGc { get; init; } = System.Runtime.GCSettings.IsServerGC;
    public string? GcLatencyMode { get; init; } = System.Runtime.GCSettings.LatencyMode.ToString();
    public int LogicalCpus { get; init; } = Environment.ProcessorCount;
    public string? FinalStateHash { get; init; }
    public string? MemorySnapshotPhase { get; init; }
    public long? AllocatorAllocatedBytes { get; init; }
    public double? AllocatorAllocatedBytesPerFrame { get; init; }
    public string AllocationScope { get; init; } = "process managed allocations; measured window";

    public void Write(string path) => File.WriteAllText(path,
        JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false }));
}