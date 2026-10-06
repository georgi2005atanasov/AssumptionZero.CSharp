using System.Diagnostics.Tracing;

namespace AssumptionZero.Infrastructure.Benchmarking;

public sealed class GcPauseListener : EventListener
{
    private readonly List<GcPause> pauses = new();
    private long pendingStart;
    private int pendingReason;

    public IReadOnlyList<GcPause> Pauses
    {
        get
        {
            lock (pauses) return pauses.ToArray();
        }
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Informational, (EventKeywords)1);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (pauses is null) return;
        if (e.EventId == 9)
        {
            int reason = e.Payload?.Count > 0 ? Convert.ToInt32(e.Payload[0]) : -1;
            if (reason is not (1 or 6)) return;
            lock (pauses)
            {
                pendingStart = e.TimeStamp.ToUniversalTime().Ticks;
                pendingReason = reason;
            }
        }
        else if (e.EventId == 3)
        {
            lock (pauses)
            {
                long end = e.TimeStamp.ToUniversalTime().Ticks;
                if (pendingStart > 0 && end >= pendingStart && pauses.Count < 100_000)
                    pauses.Add(new(pendingStart, end, pendingReason));
                pendingStart = 0;
            }
        }
    }
}
