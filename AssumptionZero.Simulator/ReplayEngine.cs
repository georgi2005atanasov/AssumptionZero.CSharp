using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json.Serialization;
using AssumptionZero.Infrastructure;

namespace AssumptionZero.Simulator;

public interface IReplayClock
{
    long Frequency { get; }
    long GetTimestamp();
    void WaitUntil(long origin, long offsetTicks, CancellationToken cancellationToken);
}

public sealed class StopwatchReplayClock : IReplayClock
{
    public long Frequency => Stopwatch.Frequency;
    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public void WaitUntil(long origin, long offsetTicks, CancellationToken cancellationToken)
    {
        long spinWindow = NanosecondTime.ToTicksCeiling(1_000_000, Frequency);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long remaining = offsetTicks - (GetTimestamp() - origin);
            if (remaining <= 0)
                return;
            if (remaining > spinWindow)
            {
                long milliseconds = (long)((Int128)(remaining - spinWindow) * 1000 / Frequency);
                if (milliseconds > 0)
                {
                    // Bound the sleep and let cancellation interrupt even a long recorded silence.
                    cancellationToken.WaitHandle.WaitOne((int)Math.Min(milliseconds, 100));
                    continue;
                }
            }
            Thread.SpinWait(16);
        }
    }
}

public sealed record TimingStatistics(long P50, long P95, long P99, long P999, long Maximum)
{
    public static TimingStatistics Calculate(long[] values, int count)
    {
        if (count == 0) return new(0, 0, 0, 0, 0);
        var sorted = values.AsSpan(0, count).ToArray();
        Array.Sort(sorted);
        long Percentile(double fraction) => sorted[(int)Math.Ceiling(count * fraction) - 1];
        return new(Percentile(.5), Percentile(.95), Percentile(.99), Percentile(.999), sorted[^1]);
    }
}

public sealed record ReplayReport(Guid RecordingId, bool Completed, int SentMessages, long SentBytes,
    long ExpectedDurationNanoseconds, long ActualDurationNanoseconds,
    TimingStatistics SendStartLateness, TimingStatistics SendDuration, string? Error,
    // Local clock origin is exposed for in-process diagnostics, not as a portable timestamp in reports.
    [property: JsonIgnore] long OriginTimestamp);

public sealed class ReplayEngine(IReplayClock clock)
{
    // The caller runs this synchronous loop on a dedicated thread. A send must complete before the next one.
    public ReplayReport Run(Recording recording, Action<ReplayMessage, CancellationToken> send,
        CancellationToken cancellationToken = default)
    {
        int count = recording.Messages.Count;
        var deadlines = new long[count];
        var lateness = new long[count];
        var durations = new long[count];
        for (int i = 0; i < count; i++)
            deadlines[i] = NanosecondTime.ToTicksCeiling(recording.Messages[i].ElapsedNanoseconds, clock.Frequency);

        int sent = 0;
        long bytes = 0;
        string? error = null;
        long origin = clock.GetTimestamp();
        try
        {
            for (int i = 0; i < count; i++)
            {
                clock.WaitUntil(origin, deadlines[i], cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                long start = clock.GetTimestamp();
                send(recording.Messages[i], cancellationToken);
                long end = clock.GetTimestamp();
                lateness[i] = NanosecondTime.FromTicks(Math.Max(0, start - origin - deadlines[i]), clock.Frequency);
                durations[i] = NanosecondTime.FromTicks(end - start, clock.Frequency);
                bytes += recording.Messages[i].Payload.Length;
                sent++;
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException)
        {
            error = exception.Message;
        }
        long elapsed = NanosecondTime.FromTicks(clock.GetTimestamp() - origin, clock.Frequency);
        return new(recording.Id, sent == count, sent, bytes, recording.DurationNanoseconds, elapsed,
            TimingStatistics.Calculate(lateness, sent), TimingStatistics.Calculate(durations, sent), error, origin);
    }
}
