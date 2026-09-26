namespace AssumptionZero.Domain.InMemory;

public readonly record struct TimedValue(long TimeNs, double Value);

public readonly record struct PendingMarkout(long DueNs, long FillPriceTicks, int Side);

/// <summary>Preallocated event-time history owned by one instrument writer.</summary>
public struct SignalMemory
{
    private readonly TimedValue[] mids;
    private readonly TimedValue[] trades;
    private readonly TimedValue[] liquidity;
    private readonly TimedValue[] losses;
    private readonly PendingMarkout[] markouts;
    private readonly double[] ofi;
    private int midHead, midCount, tradeHead, tradeCount, liquidityHead, liquidityCount;
    private int lossHead, lossCount, markoutCount, ofiHead, ofiCount;
    private double ofiSum, ofiAbsoluteSum;
    private long firstMidTime;
    private bool hasFirstMid;

    public bool Faulted { get; private set; }
    public bool Warm { get; private set; }

    public SignalMemory(in HotConfig config)
    {
        mids = new TimedValue[4_096];
        trades = new TimedValue[8_192];
        liquidity = new TimedValue[4_096];
        losses = new TimedValue[1_024];
        markouts = new PendingMarkout[1_024];
        ofi = new double[config.OfiWindow];
        midHead = midCount = tradeHead = tradeCount = liquidityHead = liquidityCount = 0;
        lossHead = lossCount = markoutCount = ofiHead = ofiCount = 0;
        ofiSum = ofiAbsoluteSum = 0;
        firstMidTime = 0;
        hasFirstMid = Faulted = Warm = false;
    }

    public void Reset()
    {
        midHead = midCount = tradeHead = tradeCount = liquidityHead = liquidityCount = 0;
        lossHead = lossCount = markoutCount = ofiHead = ofiCount = 0;
        ofiSum = ofiAbsoluteSum = 0;
        firstMidTime = 0;
        hasFirstMid = Faulted = Warm = false;
    }

    public void AddTrade(long timeNs, double quantity, bool aggressiveBuy, in HotConfig config)
    {
        if (quantity <= 0 || !double.IsFinite(quantity))
        {
            Faulted = true;
            return;
        }

        Expire(trades, ref tradeHead, ref tradeCount, timeNs - config.TradeFlowWindowNs);
        if (!Append(trades, ref tradeHead, ref tradeCount,
                new TimedValue(timeNs, aggressiveBuy ? quantity : -quantity))) Faulted = true;
    }

    public void UpdateTradeFlow(long timeNs, in HotConfig config, ref SignalState state)
    {
        Expire(trades, ref tradeHead, ref tradeCount, timeNs - config.TradeFlowWindowNs);
        double buyVolume = 0, sellVolume = 0;
        for (int i = 0; i < tradeCount; i++)
        {
            double value = trades[(tradeHead + i) % trades.Length].Value;
            if (value > 0) buyVolume += value;
            else sellVolume -= value;
        }

        state.TradeFlowImbalance = Ratio(buyVolume - sellVolume, buyVolume + sellVolume);
    }

    public void AddOfi(double value)
    {
        if (ofiCount == ofi.Length)
        {
            double old = ofi[ofiHead];
            ofiSum -= old;
            ofiAbsoluteSum -= Math.Abs(old);
            ofi[ofiHead] = value;
            ofiHead = (ofiHead + 1) % ofi.Length;
        }
        else
        {
            ofi[(ofiHead + ofiCount) % ofi.Length] = value;
            ofiCount++;
        }

        ofiSum += value;
        ofiAbsoluteSum += Math.Abs(value);
    }

    public void AddFill(long timeNs, long priceTicks, bool buy)
    {
        if (markoutCount == markouts.Length)
        {
            Faulted = true;
            return;
        }

        markouts[markoutCount++] = new PendingMarkout(checked(timeNs + 250_000_000), priceTicks, buy ? 1 : -1);
    }

    public double UpdateBook(long timeNs, in OrderBook book, in HotConfig config, ref SignalState state)
    {
        double mid = ((double)book.Bids[0].PriceTicks + book.Asks[0].PriceTicks) * 0.5;
        if (!hasFirstMid)
        {
            firstMidTime = timeNs;
            hasFirstMid = true;
        }

        long keepMid = Math.Max(config.MomentumWindowNs, config.VolatilityWindowNs);
        ExpireKeepingPredecessor(mids, ref midHead, ref midCount, timeNs - keepMid);
        if (!Append(mids, ref midHead, ref midCount, new TimedValue(timeNs, mid))) Faulted = true;

        double bestBid = book.Bids[0].Quantity, bestAsk = book.Asks[0].Quantity;
        state.MicroSignal = Ratio(bestBid - bestAsk, bestBid + bestAsk);
        double weightedBid = 0, weightedAsk = 0;
        for (int i = 0; i < config.BookLevels; i++)
        {
            weightedBid += book.Bids[i].Quantity / (double)(i + 1);
            weightedAsk += book.Asks[i].Quantity / (double)(i + 1);
        }

        state.MultiLevelImbalance = Ratio(weightedBid - weightedAsk, weightedBid + weightedAsk);
        state.OrderFlowImbalance = Ratio(ofiSum, ofiAbsoluteSum);
        state.LiquidityUnits = (long)Math.Min(weightedBid, weightedAsk);

        Expire(liquidity, ref liquidityHead, ref liquidityCount, timeNs - config.LiquidityWindowNs);
        if (!Append(liquidity, ref liquidityHead, ref liquidityCount,
                new TimedValue(timeNs, Math.Min(weightedBid, weightedAsk)))) Faulted = true;
        double depthSum = 0;
        for (int i = 0; i < liquidityCount; i++) depthSum += liquidity[(liquidityHead + i) % liquidity.Length].Value;
        double depthMean = liquidityCount == 0 ? 0 : depthSum / liquidityCount;
        state.LiquidityRatio = depthMean > 0 ? Math.Clamp(Math.Min(weightedBid, weightedAsk) / depthMean, 0, 1) : 0;

        UpdateTradeFlow(timeNs, config, ref state);

        double olderMid = mid;
        long momentumCutoff = timeNs - config.MomentumWindowNs;
        for (int i = 0; i < midCount; i++)
        {
            TimedValue sample = mids[(midHead + i) % mids.Length];
            if (sample.TimeNs > momentumCutoff) break;
            olderMid = sample.Value;
        }

        state.MomentumSignal = Math.Clamp((mid - olderMid) / config.MomentumScaleTicks, -1, 1);

        double sum = 0, sumSq = 0;
        int n = 0;
        for (int i = 0; i < midCount; i++)
        {
            TimedValue sample = mids[(midHead + i) % mids.Length];
            if (sample.TimeNs < timeNs - config.VolatilityWindowNs) continue;
            double delta = sample.Value - mid;
            sum += delta;
            sumSq += delta * delta;
            n++;
        }

        state.VolatilityTicks = n > 0 ? Math.Sqrt(Math.Max(0, sumSq / n - (sum / n) * (sum / n))) : 0;

        ResolveMarkouts(timeNs, config.TradeFlowWindowNs);
        double loss = 0;
        for (int i = 0; i < lossCount; i++) loss += losses[(lossHead + i) % losses.Length].Value;
        Warm = hasFirstMid && timeNs - firstMidTime >= config.MomentumWindowNs &&
               ofiCount == ofi.Length && liquidityCount > 0;
        return lossCount == 0 ? 0 : Math.Clamp(loss / lossCount / config.AdverseScaleTicks, 0, 1);
    }

    private void ResolveMarkouts(long timeNs, long lossWindowNs)
    {
        Expire(losses, ref lossHead, ref lossCount, timeNs - lossWindowNs);
        for (int i = 0; i < markoutCount;)
        {
            PendingMarkout pending = markouts[i];
            if (pending.DueNs > timeNs)
            {
                i++;
                continue;
            }

            bool found = false;
            double markMid = 0;
            for (int j = 0; j < midCount; j++)
            {
                TimedValue sample = mids[(midHead + j) % mids.Length];
                if (sample.TimeNs < pending.DueNs) continue;
                markMid = sample.Value;
                found = true;
                break;
            }

            if (!found)
            {
                Faulted = true;
                break;
            }

            double markout = pending.Side * (markMid - pending.FillPriceTicks);
            if (!Append(losses, ref lossHead, ref lossCount,
                    new TimedValue(timeNs, Math.Max(0, -markout)))) Faulted = true;
            markouts[i] = markouts[--markoutCount];
        }
    }

    private static double Ratio(double numerator, double denominator) => denominator > 0 ? numerator / denominator : 0;

    private static bool Append(TimedValue[] ring, ref int head, ref int count, TimedValue value)
    {
        if (count == ring.Length) return false;
        ring[(head + count++) % ring.Length] = value;
        return true;
    }

    private static void Expire(TimedValue[] ring, ref int head, ref int count, long cutoff)
    {
        while (count > 0 && ring[head].TimeNs < cutoff)
        {
            head = (head + 1) % ring.Length;
            count--;
        }
    }

    private static void ExpireKeepingPredecessor(TimedValue[] ring, ref int head, ref int count, long cutoff)
    {
        while (count > 1 && ring[(head + 1) % ring.Length].TimeNs <= cutoff)
        {
            head = (head + 1) % ring.Length;
            count--;
        }
    }
}