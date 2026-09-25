using System.Net.WebSockets;
using AssumptionZero.Infrastructure;
using AssumptionZero.Simulator;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AssumptionZero.Tests;

[TestClass]
public class ReplayEngineTests
{
    internal static Recording Capture(params long[] offsets) => new(Guid.NewGuid(),
        offsets.Select((offset, i) => new ReplayMessage(i + 1, offset, WebSocketMessageType.Binary, [(byte)i])));

    private sealed class FakeClock(long frequency = 1_000_000_000) : IReplayClock
    {
        public long Frequency => frequency;
        public long Now = 100;
        public long GetTimestamp() => Now;
        public void WaitUntil(long origin, long offsetTicks, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Now = Math.Max(Now, origin + offsetTicks);
        }
    }

    [TestMethod]
    public void PreservesInitialDelayEqualTimestampsAndSubMicrosecondGaps()
    {
        var clock = new FakeClock();
        long origin = clock.Now;
        var offsets = new long[] { 123, 123, 124, 700, 3_600_000_000_000 };
        var actual = new List<long>();
        var sequences = new List<long>();
        var report = new ReplayEngine(clock).Run(Capture(offsets), (message, _) =>
        {
            actual.Add(clock.Now - origin);
            sequences.Add(message.SequenceNumber);
        });
        CollectionAssert.AreEqual(offsets, actual);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5 }, sequences);
        Assert.IsTrue(report.Completed);
        Assert.AreEqual(0L, report.SendStartLateness.Maximum);
        Assert.AreEqual(offsets[^1], report.ActualDurationNanoseconds);
    }

    [TestMethod]
    public void SlowSendsCatchUpWithoutDroppingOrMovingFutureDeadlines()
    {
        var clock = new FakeClock();
        long origin = clock.Now;
        var actual = new List<long>();
        var report = new ReplayEngine(clock).Run(Capture(10, 20, 30, 100), (_, _) =>
        {
            actual.Add(clock.Now - origin);
            clock.Now += 25;
        });
        CollectionAssert.AreEqual(new long[] { 10, 35, 60, 100 }, actual);
        Assert.AreEqual(4, report.SentMessages);
        Assert.AreEqual(30L, report.SendStartLateness.Maximum);
        Assert.AreEqual(25L, report.SendDuration.Maximum);
        Assert.AreEqual(125L, report.ActualDurationNanoseconds);
    }

    [TestMethod]
    public void SendCostsDoNotAccumulateDrift()
    {
        var clock = new FakeClock();
        var report = new ReplayEngine(clock).Run(Capture(100, 200, 300, 400), (_, _) => clock.Now += 7);
        Assert.AreEqual(407L, report.ActualDurationNanoseconds);
        Assert.AreEqual(0L, report.SendStartLateness.Maximum);
    }

    [TestMethod]
    public void RoundsDeadlinesUpAtLowerClockResolution()
    {
        var clock = new FakeClock(10_000_000);
        long origin = clock.Now;
        var actual = new List<long>();
        new ReplayEngine(clock).Run(Capture(1, 100, 101), (_, _) => actual.Add(clock.Now - origin));
        CollectionAssert.AreEqual(new long[] { 1, 1, 2 }, actual);
    }

    [TestMethod]
    public void ConversionAvoidsOverflowAndPreservesAvailablePrecision()
    {
        Assert.AreEqual(long.MaxValue, NanosecondTime.ToTicksCeiling(long.MaxValue, 1_000_000_000));
        Assert.AreEqual(long.MaxValue, NanosecondTime.FromTicks(long.MaxValue, 1_000_000_000));
        Assert.AreEqual(123L, NanosecondTime.FromTicks(123, 1_000_000_000));
        Assert.ThrowsException<OverflowException>(() => NanosecondTime.ToTicksCeiling(long.MaxValue, long.MaxValue));
    }

    [TestMethod]
    public void CancellationAndSendFailureProduceIncompleteReports()
    {
        using var cancellation = new CancellationTokenSource();
        var report = new ReplayEngine(new FakeClock()).Run(Capture(0, 100), (_, _) => cancellation.Cancel(), cancellation.Token);
        Assert.IsFalse(report.Completed);
        Assert.AreEqual(1, report.SentMessages);
        Assert.IsNotNull(report.Error);
        var failed = new ReplayEngine(new FakeClock()).Run(Capture(0), (_, _) => throw new IOException("Disconnected"));
        Assert.IsFalse(failed.Completed);
        Assert.AreEqual(0, failed.SentMessages);
        Assert.AreEqual(0L, failed.SentBytes);
    }

    [TestMethod]
    public void InvalidRecordingsAreRejectedBeforeReplay()
    {
        Assert.ThrowsException<InvalidDataException>(() => Capture());
        Assert.ThrowsException<InvalidDataException>(() => Capture(-1));
        Assert.ThrowsException<InvalidDataException>(() => Capture(100, 99));
        var valid = Capture(0).Messages[0];
        foreach (var invalid in new[]
                 {
                     valid with { SequenceNumber = 2 },
                     valid with { MessageType = WebSocketMessageType.Close },
                     valid with { MessageType = (WebSocketMessageType)99 },
                     valid with { Payload = new byte[1024 * 1024 + 1] }
                 })
            Assert.ThrowsException<InvalidDataException>(() => new Recording(Guid.NewGuid(), [invalid]));
        Assert.ThrowsException<InvalidDataException>(() => new Recording(Guid.NewGuid(),
            [valid, valid with { SequenceNumber = 3 }]));
        _ = new Recording(Guid.NewGuid(), [valid with { Payload = new byte[1024 * 1024] }]);
    }
}
