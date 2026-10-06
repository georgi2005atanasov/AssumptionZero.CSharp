using System.Diagnostics;
using System.Net.WebSockets;
using AssumptionZero.Infrastructure.Benchmarking;
using AssumptionZero.Simulator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AssumptionZero.Tests;

[TestClass]
public class BenchmarkingTests
{
    [TestMethod]
    [DoNotParallelize]
    public void MemorySnapshotExcludesReportConstruction()
    {
        using var benchmark = new ReplayBenchmarkRecorder(20_000, 2_000, diagnostic: false);
        for (int i = 0; i < 20_000; i++)
        {
            benchmark.BeginFrame(WebSocketMessageType.Binary, 100, Stopwatch.GetTimestamp());
            benchmark.AddDecode(1);
            benchmark.AddBook(1);
            benchmark.AddSignalAndQuote(1, 1);
            benchmark.EndFrame();
        }
        // Prevent a collection from hiding the report-only allocations this test detects.
        Assert.IsTrue(GC.TryStartNoGCRegion(64 * 1024 * 1024));
        try
        {
            long before = GC.GetTotalMemory(false);
            BenchmarkAppReport report = benchmark.Finish(true);
            long after = GC.GetTotalMemory(false);
            Assert.IsTrue(report.ManagedHeapBytes <= before + 1024 * 1024,
                "The memory snapshot includes report-construction allocations.");
            Assert.IsTrue(after > before + 2 * 1024 * 1024,
                "The diagnostic must exercise substantial report-only allocation.");
            Assert.AreEqual("after_replay_before_report", report.MemorySnapshotPhase);
            Assert.AreEqual(report.MemorySnapshotPhase, BenchmarkAppOverview.From(report).MemorySnapshotPhase);
            GC.KeepAlive(report);
        }
        finally { GC.EndNoGCRegion(); }
    }

    [TestMethod]
    public void DiagnosticListenerCapturesAnIndividualGcPause()
    {
        using var listener = new GcPauseListener();
        byte[] allocated = new byte[1024 * 1024];
        allocated[0] = 1;
        GC.KeepAlive(allocated);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Assert.IsTrue(SpinWait.SpinUntil(() => listener.Pauses.Count > 0, TimeSpan.FromSeconds(2)),
            "The GC diagnostic listener did not receive a pause event.");
        Assert.IsTrue(listener.Pauses.All(p => p.EndUtcTicks >= p.StartUtcTicks));
    }

    [TestMethod]
    public void DiagnosticReportMarksFrameOverlappingGcPause()
    {
        using var benchmark = new ReplayBenchmarkRecorder(2, 0, diagnostic: true);
        benchmark.BeginFrame(WebSocketMessageType.Binary, 32, Stopwatch.GetTimestamp());
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        benchmark.EndFrame();
        Assert.IsTrue(SpinWait.SpinUntil(() => benchmark.Finish(true).GcPauses.Length > 0,
            TimeSpan.FromSeconds(2)));
        BenchmarkAppReport report = benchmark.Finish(true);
        Assert.IsTrue(report.Frames[0].GcPauseOverlap);
    }

    [TestMethod]
    public void SendToDecisionDetectsBacklogHiddenByFastClientProcessing()
    {
        var frames = new[]
        {
            new FrameSample { ReceiveTicks = 0, LastDecisionTicks = 100, ProcessingEndTicks = 100 },
            new FrameSample { ReceiveTicks = 4_900, LastDecisionTicks = 5_000, ProcessingEndTicks = 5_000 },
            new FrameSample { ReceiveTicks = 9_900, LastDecisionTicks = 10_000, ProcessingEndTicks = 10_000 }
        };
        var (decision, processing) = BenchmarkAnalysis.Summarize(frames, [0, 1_000, 2_000],
            warmup: 0, sent: 3, frequency: 1_000_000_000);
        Assert.AreEqual(8_000L, decision.P99);
        Assert.AreEqual(8_000L, processing.P99);
        Assert.AreEqual(100L, frames[2].LastDecisionTicks - frames[2].ReceiveTicks);
    }

    [TestMethod]
    public void InterruptedRunBeforeWarmupStillWritesIncompleteReport()
    {
        using var benchmark = new ReplayBenchmarkRecorder(10, 5, diagnostic: false);
        benchmark.BeginFrame(WebSocketMessageType.Binary, 16, Stopwatch.GetTimestamp());
        benchmark.EndFrame();
        BenchmarkAppReport report = benchmark.Finish(false);
        Assert.IsFalse(report.Completed);
        Assert.AreEqual(1, report.ReceivedFrames);
        Assert.AreEqual(0, report.MeasuredFrames);
        Assert.AreEqual(0, report.DecodeNanoseconds.Count);
    }
}
