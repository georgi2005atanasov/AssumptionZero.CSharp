namespace AssumptionZero.Infrastructure.Benchmarking;

public sealed record BenchmarkAppOverview(
    int ReceivedFrames,
    int TotalDecisions,
    int MeasuredFrames,
    int MeasuredDecisions,
    long MeasuredBytes,
    long MeasuredDurationNanoseconds,
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
    int FramesOverlappingGc)
{
    public string? MemorySnapshotPhase { get; init; }
    public long? AllocatorAllocatedBytes { get; init; }
    public double? AllocatorAllocatedBytesPerFrame { get; init; }
    public string? AllocationScope { get; init; }

    public static BenchmarkAppOverview From(BenchmarkAppReport app) => new(
        app.ReceivedFrames, app.TotalDecisions, app.MeasuredFrames, app.MeasuredDecisions,
        app.MeasuredBytes, app.MeasuredDurationNanoseconds, app.DecodeNanoseconds,
        app.BookNanoseconds, app.SignalNanoseconds, app.QuoteNanoseconds, app.CpuNanoseconds,
        app.CpuCores, app.CpuNanosecondsPerFrame, app.ResidentBytes, app.PeakResidentBytes,
        app.ManagedHeapBytes, app.ManagedAllocatedBytes, app.ManagedAllocatedBytesPerFrame,
        app.GcCollections, app.GcPauseNanoseconds, app.IndividualGcPauseNanoseconds,
        app.Frames.Count(f => f.GcPauseOverlap))
    {
        AllocatorAllocatedBytes = app.AllocatorAllocatedBytes,
        AllocatorAllocatedBytesPerFrame = app.AllocatorAllocatedBytesPerFrame, AllocationScope = app.AllocationScope,
        MemorySnapshotPhase = app.MemorySnapshotPhase
    };
}