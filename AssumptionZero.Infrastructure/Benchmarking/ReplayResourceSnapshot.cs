namespace AssumptionZero.Infrastructure.Benchmarking;

internal readonly record struct ReplayResourceSnapshot(
    long CpuTicks,
    long Allocated,
    long GcPauseTicks,
    long ResidentBytes,
    long PeakResidentBytes,
    long ManagedHeapBytes,
    int[] GcCounts);
