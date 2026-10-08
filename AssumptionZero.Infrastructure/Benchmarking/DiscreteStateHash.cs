using AssumptionZero.Application.Services;
using AssumptionZero.Domain.InMemory;

namespace AssumptionZero.Infrastructure.Benchmarking;

/// <summary>Canonical discrete state, shared with Rust. Deliberately excludes floating intermediates.</summary>
public static class DiscreteStateHash
{

    public static string Compute(InstrumentRuntime[] runtimes)
    {
        ulong hash = 14695981039346656037UL;
        Mix(ref hash, runtimes.Length);
        Mix(ref hash, runtimes.Length > 0 && runtimes[0].AccountReady ? 1 : 0);
        foreach (InstrumentRuntime runtime in runtimes)
        {
            MixInstrument(ref hash, runtime);
        }

        return hash.ToString("x16");
    }

    private static void MixInstrument(ref ulong hash, InstrumentRuntime runtime)
    {
        OrderBook book = runtime.OrderBook;
        InventoryState inventory = runtime.Inventory;
        long[] state = [runtime.Instrument.Id, book.LastUpdateId, book.IsSynchronized ? 1 : 0,
            runtime.SignalMemory.Warm ? 1 : 0, runtime.SignalMemory.Faulted ? 1 : 0, inventory.CurrentQuantity,
            inventory.TargetQuantity, inventory.MaxLongQuantity, inventory.MaxShortQuantity,
            inventory.ReservedBuyQuantity, inventory.ReservedSellQuantity];
        foreach (long value in state)
        {
            Mix(ref hash, value);
        }

        MixLevels(ref hash, book.Bids, book.BidCount);
        MixLevels(ref hash, book.Asks, book.AskCount);
        foreach (WorkingOrder order in new[]
        {
            runtime.WorkingOrders.BidOrder,
            runtime.WorkingOrders.AskOrder
        }

        )
        {
            MixOrder(ref hash, order);
        }
    }

    private static void MixLevels(ref ulong hash, BookLevel[] levels, int count)
    {
        Mix(ref hash, count);
        for (int i = 0; i < count; i++)
        {
            Mix(ref hash, levels[i].PriceTicks);
            Mix(ref hash, levels[i].Quantity);
        }
    }

    private static void MixOrder(ref ulong hash, in WorkingOrder order)
    {
        Mix(ref hash, order.OrderId);
        Mix(ref hash, order.PriceTicks);
        Mix(ref hash, order.Quantity);
        Mix(ref hash, order.FilledQuantity);
        Mix(ref hash, (long)order.Status);
        Mix(ref hash, order.IsActive ? 1 : 0);
    }

    private static void Mix(ref ulong hash, long value) => hash = unchecked((hash ^ (ulong)value) * 1099511628211UL);
}
