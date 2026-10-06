using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net.WebSockets;
using System.Text.Json;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.InMemory;

namespace AssumptionZero.Infrastructure.Benchmarking;

public sealed class ReplayBenchmarkRecorder : IDisposable
{
    private readonly FrameSample[] frames;
    private readonly int warmupFrames;
    private readonly GcPauseListener? gcListener;
    private readonly Process process = Process.GetCurrentProcess();
    private long startCpuTicks, startAllocated, startGcPauseTicks;
    private readonly int[] startGcCounts = new int[3];
    private long baseUtcTicks, baseStopwatchTicks;
    private int count;
    private ulong decisionHash = 14695981039346656037UL;
    private int totalDecisions;

    public ReplayBenchmarkRecorder(int expectedFrames, int warmupFrames, bool diagnostic)
    {
        if (expectedFrames <= 0 || warmupFrames < 0 || warmupFrames >= expectedFrames)
            throw new ArgumentOutOfRangeException(nameof(expectedFrames));
        frames = new FrameSample[expectedFrames];
        this.warmupFrames = warmupFrames;
        if (diagnostic) gcListener = new GcPauseListener();
        if (warmupFrames == 0) CaptureStart();
    }

    public int Count => count;

    public void BeginFrame(WebSocketMessageType type, int bytes, long receiveTicks)
    {
        if (count == frames.Length) throw new InvalidDataException("Received more frames than expected.");
        frames[count] = new FrameSample
        {
            Index = ++count, MessageType = type, Bytes = bytes, ReceiveTicks = receiveTicks
        };
    }

    public void EndFrame()
    {
        frames[count - 1].ProcessingEndTicks = Stopwatch.GetTimestamp();
        if (count == warmupFrames) CaptureStart();
    }

    public void SetTemplate(int templateId) => frames[count - 1].TemplateId = templateId;
    public void AddDecode(long ticks) => frames[count - 1].DecodeTicks += ticks;
    public void AddBook(long ticks) => frames[count - 1].BookTicks += ticks;

    public void AddSignalAndQuote(long signalTicks, long quoteTicks)
    {
        frames[count - 1].SignalTicks += signalTicks;
        frames[count - 1].QuoteTicks += quoteTicks;
    }

    public void Decision(InstrumentRuntime runtime, in QuoteDecision decision)
    {
        ref FrameSample frame = ref frames[count - 1];
        frame.Decisions++;
        frame.LastDecisionTicks = Stopwatch.GetTimestamp();
        totalDecisions++;
        Mix(runtime.Instrument.Id);
        Mix(decision.BidPriceTicks);
        Mix(decision.BidQuantity);
        Mix((long)decision.BidAction);
        Mix(decision.AskPriceTicks);
        Mix(decision.AskQuantity);
        Mix((long)decision.AskAction);
    }

    private void Mix(long value)
    {
        decisionHash = (decisionHash ^ (ulong)value) * 1099511628211UL;
    }

    private void CaptureStart()
    {
        baseStopwatchTicks = Stopwatch.GetTimestamp();
        baseUtcTicks = DateTime.UtcNow.Ticks;
        process.Refresh();
        startCpuTicks = process.TotalProcessorTime.Ticks;
        startAllocated = GC.GetTotalAllocatedBytes(false);
        startGcPauseTicks = GC.GetTotalPauseDuration().Ticks;
        for (int i = 0; i < 3; i++) startGcCounts[i] = GC.CollectionCount(i);
    }

    public BenchmarkAppReport Finish(bool completed)
    {
        if (baseUtcTicks == 0) CaptureStart();
        process.Refresh();
        long cpuTicks = process.TotalProcessorTime.Ticks - startCpuTicks;
        long allocated = GC.GetTotalAllocatedBytes(false) - startAllocated;
        long gcPauseTicks = GC.GetTotalPauseDuration().Ticks - startGcPauseTicks;
        // Match Rust's boundary: snapshot memory before allocating any report buffers.
        long residentBytes = process.WorkingSet64;
        long peakResidentBytes = process.PeakWorkingSet64;
        long managedHeapBytes = GC.GetTotalMemory(false);
        int[] gcCounts = Enumerable.Range(0, 3).Select(i => GC.CollectionCount(i) - startGcCounts[i]).ToArray();
        FrameSample[] recorded = frames.AsSpan(0, count).ToArray();
        long finishUtcTicks = DateTime.UtcNow.Ticks;
        var pauses = gcListener?.Pauses.Where(p => p.EndUtcTicks >= baseUtcTicks &&
                                                   p.StartUtcTicks <= finishUtcTicks).ToArray() ?? [];
        if (pauses.Length > 0)
            for (int i = warmupFrames; i < recorded.Length; i++)
            {
                long start = baseUtcTicks + (recorded[i].ReceiveTicks - baseStopwatchTicks) * TimeSpan.TicksPerSecond /
                    Stopwatch.Frequency;
                long end = baseUtcTicks + (recorded[i].ProcessingEndTicks - baseStopwatchTicks) *
                    TimeSpan.TicksPerSecond / Stopwatch.Frequency;
                recorded[i].GcPauseOverlap = pauses.Any(p => p.StartUtcTicks <= end && p.EndUtcTicks >= start);
            }

        int measuredCount = Math.Max(0, count - warmupFrames);
        var processing = new long[measuredCount];
        var receiveToDecision = new long[measuredCount];
        int withDecision = 0, measuredDecisions = 0;
        long measuredBytes = 0;
        for (int i = warmupFrames; i < count; i++)
        {
            FrameSample f = recorded[i];
            processing[i - warmupFrames] =
                NanosecondTime.FromTicks(f.ProcessingEndTicks - f.ReceiveTicks, Stopwatch.Frequency);
            if (f.LastDecisionTicks != 0)
                receiveToDecision[withDecision++] =
                    NanosecondTime.FromTicks(f.LastDecisionTicks - f.ReceiveTicks, Stopwatch.Frequency);
            measuredDecisions += f.Decisions;
            measuredBytes += f.Bytes;
        }

        long durationNs = measuredCount == 0
            ? 0
            : NanosecondTime.FromTicks(
                recorded[^1].ProcessingEndTicks - recorded[warmupFrames].ReceiveTicks, Stopwatch.Frequency);

        LatencySummary Stage(Func<FrameSample, long> select)
        {
            long[] values = recorded.AsSpan(Math.Min(warmupFrames, recorded.Length)).ToArray()
                .Select(select).Where(v => v > 0)
                .Select(v => NanosecondTime.FromTicks(v, Stopwatch.Frequency)).ToArray();
            return LatencySummary.From(values, values.Length);
        }

        long[] pauseDurations = pauses.Select(p => Math.Max(0, p.EndUtcTicks - p.StartUtcTicks) * 100).ToArray();
        return new BenchmarkAppReport(3, completed, Stopwatch.Frequency, warmupFrames, count,
            totalDecisions, decisionHash.ToString("x16"), measuredCount, measuredDecisions, measuredBytes,
            durationNs, LatencySummary.From(processing, processing.Length),
            LatencySummary.From(receiveToDecision, withDecision),
            Stage(f => f.DecodeTicks), Stage(f => f.BookTicks),
            Stage(f => f.SignalTicks), Stage(f => f.QuoteTicks),
            cpuTicks * 100L, durationNs == 0 ? 0 : cpuTicks * 100.0 / durationNs,
            measuredCount == 0 ? 0 : cpuTicks * 100.0 / measuredCount,
            residentBytes, peakResidentBytes, managedHeapBytes,
            allocated, measuredCount == 0 ? 0 : (double)allocated / measuredCount,
            gcCounts, gcPauseTicks * 100L, LatencySummary.From(pauseDurations, pauseDurations.Length),
            gcListener is not null,
            pauses, recorded) { MemorySnapshotPhase = "after_replay_before_report" };
    }

    public void Dispose()
    {
        gcListener?.Dispose();
        process.Dispose();
    }
}