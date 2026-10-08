using System.Text.Json;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.InMemory;

namespace AssumptionZero.App;

internal sealed class ReplayDecisionObserver(StreamWriter? trace, bool measureAllocations)
{
    private int decisions, actionable, readyDecisions;
    private long allocationStart;
    private long allocated = -1;

    public void RecordDecision(InstrumentRuntime runtime, QuoteDecision decision)
    {
        decisions++;
        if (trace is not null)
        {
            trace.WriteLine(JsonSerializer.Serialize(new long[] { runtime.Instrument.Id, decision.BidPriceTicks,
                decision.BidQuantity, (long)decision.BidAction, decision.AskPriceTicks, decision.AskQuantity,
                (long)decision.AskAction }));
        }

        if (decision.BidAction is QuoteAction.Place or QuoteAction.Replace or QuoteAction.Cancel ||
            decision.AskAction is QuoteAction.Place or QuoteAction.Replace or QuoteAction.Cancel)
        {
            actionable++;
        }

        if (!measureAllocations || !runtime.Ready)
        {
            return;
        }

        readyDecisions++;
        if (readyDecisions == 100)
        {
            allocationStart = GC.GetTotalAllocatedBytes(false);
        }

        if (readyDecisions == 200)
        {
            allocated = GC.GetTotalAllocatedBytes(false) - allocationStart;
        }
    }

    public void WriteSummary(InstrumentRuntime[] runtimes)
    {
        Console.WriteLine($"Replay complete: {decisions} decisions, {actionable} with place/replace/cancel intents.");
        Console.WriteLine($"Products: {runtimes.Count(runtime => runtime.OrderBook.IsSynchronized)} " +
            $"with a book, {runtimes.Count(runtime => runtime.Ready)} ready " + $"of {runtimes.Length} configured.");
        if (measureAllocations)
        {
            Console.WriteLine($"MARKET_ALLOCATED_BYTES={allocated};READY_DECISIONS={readyDecisions}");
        }
    }
}
