namespace AssumptionZero.Domain.InMemory;

/// <summary>A bounded, price-sorted L2 book. A quantity of zero removes a level.</summary>
public struct OrderBook(int capacity)
{
    public BookLevel[] Bids { get; } = new BookLevel[capacity];
    public BookLevel[] Asks { get; } = new BookLevel[capacity];
    public int BidCount { get; private set; } = 0;
    public int AskCount { get; private set; } = 0;
    public long LastUpdateId { get; private set; } = 0;
    public bool IsSynchronized { get; private set; } = false;

    public void Invalidate() => IsSynchronized = false;

    public bool LoadSnapshot(ReadOnlySpan<BookLevel> bids, ReadOnlySpan<BookLevel> asks, long updateId)
    {
        BidCount = AskCount = 0;
        IsSynchronized = false;
        if (updateId <= 0 || bids.Length > Bids.Length || asks.Length > Asks.Length)
        {
            return false;
        }

        foreach (ref readonly BookLevel level in bids)
        {
            if (!Apply(level, true))
            {
                return false;
            }
        }

        foreach (ref readonly BookLevel level in asks)
        {
            if (!Apply(level, false))
            {
                return false;
            }
        }

        LastUpdateId = updateId;
        IsSynchronized = BidCount != 0 && AskCount != 0 && Bids[0].PriceTicks < Asks[0].PriceTicks;
        return IsSynchronized;
    }

    public bool CanApply(long firstId, long lastId)
    {
        if (!IsSynchronized || firstId <= 0 || lastId < firstId)
        {
            return false;
        }

        if (lastId <= LastUpdateId)
        {
            return true;
        }

        return firstId <= LastUpdateId + 1 && lastId >= LastUpdateId + 1;
    }

    public bool ApplyBid(long priceTicks, long quantity) => Apply(new BookLevel { PriceTicks = priceTicks,
        Quantity = quantity }, true);

    public bool ApplyAsk(long priceTicks, long quantity) => Apply(new BookLevel { PriceTicks = priceTicks,
        Quantity = quantity }, false);

    public bool CompleteDiff(long lastId, int requiredLevels)
    {
        LastUpdateId = lastId;
        IsSynchronized = BidCount >= requiredLevels && AskCount >= requiredLevels &&
            Bids[0].PriceTicks < Asks[0].PriceTicks;
        return IsSynchronized;
    }

    private bool Apply(BookLevel level, bool bid)
    {
        if (level.PriceTicks <= 0 || level.Quantity < 0)
        {
            return false;
        }

        BookLevel[] side = bid ? Bids : Asks;
        int count = bid ? BidCount : AskCount;
        int lo = FindLevel(side, count, level.PriceTicks, bid);
        bool found = lo < count && side[lo].PriceTicks == level.PriceTicks;
        if (found)
        {
            if (!UpdateExistingLevel(side, lo, level, ref count))
            {
                return false;
            }
        }
        else if (level.Quantity != 0)
        {
            if (lo == side.Length)
            {
                return true;
            }

            if (count < side.Length)
            {
                count++;
            }

            Array.Copy(side, lo, side, lo + 1, count - lo - 1);
            side[lo] = level;
        }

        if (bid)
        {
            BidCount = count;
        }
        else
        {
            AskCount = count;
        }

        return true;
    }

    private static int FindLevel(BookLevel[] side, int count, long priceTicks, bool bid)
    {
        int lo = 0, hi = count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            bool before = bid ? side[mid].PriceTicks > priceTicks : side[mid].PriceTicks < priceTicks;
            if (before)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    private static bool UpdateExistingLevel(BookLevel[] side, int lo, BookLevel level, ref int count)
    {
        if (level.Quantity == 0)
        {
            // A full bounded side may have an unseen level just beyond the tail.
            // Removing any tracked level then makes the top-of-book coverage unknown.
            if (count == side.Length)
            {
                return false;
            }

            Array.Copy(side, lo + 1, side, lo, count - lo - 1);
            count--;
        }
        else
        {
            side[lo] = level;
        }

        return true;
    }
}
