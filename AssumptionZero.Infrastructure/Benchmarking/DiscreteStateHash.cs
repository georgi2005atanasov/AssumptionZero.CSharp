using AssumptionZero.Application.Services;
namespace AssumptionZero.Infrastructure.Benchmarking;

/// <summary>Canonical discrete state, shared with Rust. Deliberately excludes floating intermediates.</summary>
public static class DiscreteStateHash
{
    public static string Compute(InstrumentRuntime[] runtimes)
    {
        ulong hash = 14695981039346656037UL;
        void Mix(long value) => hash = unchecked((hash ^ (ulong)value) * 1099511628211UL);
        Mix(runtimes.Length);
        Mix(runtimes.Length > 0 && runtimes[0].AccountReady ? 1 : 0);
        foreach (var r in runtimes)
        {
            var b = r.OrderBook;
            var v = r.Inventory;
            foreach (long x in new long[] { r.Instrument.Id, b.LastUpdateId, b.IsSynchronized ? 1 : 0,
                         r.SignalMemory.Warm ? 1 : 0, r.SignalMemory.Faulted ? 1 : 0,
                         v.CurrentQuantity, v.TargetQuantity, v.MaxLongQuantity, v.MaxShortQuantity,
                         v.ReservedBuyQuantity, v.ReservedSellQuantity }) Mix(x);
            Mix(b.BidCount);
            for (int i = 0; i < b.BidCount; i++) { Mix(b.Bids[i].PriceTicks); Mix(b.Bids[i].Quantity); }
            Mix(b.AskCount);
            for (int i = 0; i < b.AskCount; i++) { Mix(b.Asks[i].PriceTicks); Mix(b.Asks[i].Quantity); }
            foreach (var order in new[] { r.WorkingOrders.BidOrder, r.WorkingOrders.AskOrder })
            {
                Mix(order.OrderId); Mix(order.PriceTicks); Mix(order.Quantity); Mix(order.FilledQuantity);
                Mix((long)order.Status); Mix(order.IsActive ? 1 : 0);
            }
        }
        return hash.ToString("x16");
    }
}
