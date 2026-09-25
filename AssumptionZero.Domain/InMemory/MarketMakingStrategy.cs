using AssumptionZero.Domain.Entities;

namespace AssumptionZero.Domain.InMemory;

public sealed class MarketMakingStrategy : IStrategy
{
    public QuoteDecision Evaluate(Instrument instrument, in HotConfig config, in OrderBook book,
        in SignalState signals, in InventoryState inventory, in WorkingOrders orders,
        ref PricingState pricing, double adverseScore, decimal makerFeeRate,
        decimal freeBase, decimal freeQuote)
    {
        if (!book.IsSynchronized || book.BidCount < config.BookLevels || book.AskCount < config.BookLevels ||
            inventory.MaxLongQuantity < inventory.CurrentQuantity || makerFeeRate < 0)
            return Invalid(orders);

        double weights = config.MicroWeight + config.MliWeight + config.OfiWeight +
                         config.TradeFlowWeight + config.MomentumWeight;
        double direction = (config.MicroWeight * signals.MicroSignal +
                            config.MliWeight * signals.MultiLevelImbalance +
                            config.OfiWeight * signals.OrderFlowImbalance +
                            config.TradeFlowWeight * signals.TradeFlowImbalance +
                            config.MomentumWeight * signals.MomentumSignal) / weights;
        direction = Math.Clamp(direction, -1, 1);
        double mid = ((double)book.Bids[0].PriceTicks + book.Asks[0].PriceTicks) * 0.5;
        long deviation = inventory.CurrentQuantity - inventory.TargetQuantity;
        long lowerBound = -inventory.MaxShortQuantity;
        double range = deviation >= 0 ? inventory.MaxLongQuantity - (double)inventory.TargetQuantity :
            inventory.TargetQuantity - (double)lowerBound;
        double z = range > 0 ? Math.Clamp(deviation / range, -1, 1) : 0;
        double volatility = Math.Clamp(signals.VolatilityTicks / config.VolatilityScaleTicks, 0, 1);
        double thinness = Math.Clamp(1 - signals.LiquidityRatio, 0, 1);
        adverseScore = Math.Clamp(adverseScore, 0, 1);
        double riskSpread = config.VolatilitySpreadWeightTicks * volatility +
                            config.LiquiditySpreadWeightTicks * thinness +
                            config.AdverseSpreadWeightTicks * adverseScore;
        double halfSpread = Math.Max(
            config.BaseHalfSpreadTicks + riskSpread + config.InventorySpreadWeightTicks * Math.Abs(z),
            (double)makerFeeRate * mid + config.MinEdgeTicks);
        double fair = mid + config.MaxFairShiftTicks * direction;
        double skew = config.MaxInventorySkewTicks * z;
        double reservation = fair - skew;
        long bid = (long)Math.Floor(reservation - halfSpread);
        long ask = (long)Math.Ceiling(reservation + halfSpread);
        bid = Math.Min(bid, book.Asks[0].PriceTicks - 1);
        ask = Math.Max(ask, book.Bids[0].PriceTicks + 1);
        if (bid <= 0 || ask <= bid || !double.IsFinite(halfSpread)) return Invalid(orders);

        double riskFactor = Math.Max(config.MinRiskSizeFactor,
            1 / (1 + riskSpread / config.BaseHalfSpreadTicks));
        long desiredBuy = (long)Math.Floor(config.BaseQuantity * riskFactor * (1 - z));
        long desiredSell = (long)Math.Floor(config.BaseQuantity * riskFactor * (1 + z));
        long ownBuy = Remaining(orders.BidOrder);
        long ownSell = Remaining(orders.AskOrder);
        long otherBuy = Math.Max(0, inventory.ReservedBuyQuantity - ownBuy);
        long otherSell = Math.Max(0, inventory.ReservedSellQuantity - ownSell);
        long buyCapacity = Math.Max(0, inventory.MaxLongQuantity - inventory.CurrentQuantity - otherBuy);
        long sellCapacity = Math.Max(0, inventory.CurrentQuantity - lowerBound - otherSell);
        decimal buyFunds = freeQuote + instrument.Price(orders.BidOrder.PriceTicks) * instrument.Quantity(ownBuy);
        decimal sellFunds = freeBase + instrument.Quantity(ownSell);
        long maxUnits = instrument.ToQuantityUnits(instrument.MaxQuantity);
        long buyFundsUnits = instrument.ToQuantityUnits(Math.Min(instrument.MaxQuantity,
            buyFunds / instrument.Price(bid)));
        long sellFundsUnits = instrument.ToQuantityUnits(Math.Min(instrument.MaxQuantity, sellFunds));
        desiredBuy = Math.Max(0, Math.Min(Math.Min(desiredBuy, buyCapacity), Math.Min(buyFundsUnits, maxUnits)));
        desiredSell = Math.Max(0, Math.Min(Math.Min(desiredSell, sellCapacity), Math.Min(sellFundsUnits, maxUnits)));
        if (!ValidQuantity(instrument, bid, desiredBuy)) desiredBuy = 0;
        if (!ValidQuantity(instrument, ask, desiredSell)) desiredSell = 0;

        pricing.DirectionScore = direction;
        pricing.FairTicks = fair;
        pricing.InventorySkewTicks = skew;
        pricing.ReservationTicks = reservation;
        pricing.HalfSpreadTicks = halfSpread;
        pricing.TargetBidTicks = desiredBuy > 0 ? bid : 0;
        pricing.TargetAskTicks = desiredSell > 0 ? ask : 0;
        pricing.TargetBidQuantity = desiredBuy;
        pricing.TargetAskQuantity = desiredSell;
        return new QuoteDecision(pricing.TargetBidTicks, desiredBuy,
            Action(orders.BidOrder, pricing.TargetBidTicks, desiredBuy, config.MinimumReplaceMoveTicks),
            pricing.TargetAskTicks, desiredSell,
            Action(orders.AskOrder, pricing.TargetAskTicks, desiredSell, config.MinimumReplaceMoveTicks));
    }

    private static long Remaining(in WorkingOrder order) => order.IsActive ? Math.Max(0, order.Quantity - order.FilledQuantity) : 0;

    private static bool ValidQuantity(Instrument instrument, long price, long quantity)
    {
        if (quantity <= 0) return false;
        decimal actual = instrument.Quantity(quantity);
        decimal actualPrice = instrument.Price(price);
        return actualPrice >= instrument.MinPrice && actualPrice <= instrument.MaxPrice &&
               actual >= instrument.MinQuantity && actual <= instrument.MaxQuantity &&
               actualPrice * actual >= instrument.MinNotional;
    }

    private static QuoteAction Action(in WorkingOrder current, long targetPrice, long targetQuantity, long replaceTicks)
    {
        if (current.IsActive && current.Status is OrderStatus.PendingNew or OrderStatus.PendingCancel)
            return QuoteAction.None;
        if (targetQuantity == 0) return current.IsActive ? QuoteAction.Cancel : QuoteAction.None;
        if (!current.IsActive) return QuoteAction.Place;
        return Math.Abs(targetPrice - current.PriceTicks) >= replaceTicks ||
               targetQuantity != Remaining(current) ? QuoteAction.Replace : QuoteAction.Keep;
    }

    private static QuoteDecision Invalid(in WorkingOrders orders) => new(0, 0,
        CancelIfNeeded(orders.BidOrder),
        0, 0, CancelIfNeeded(orders.AskOrder));

    private static QuoteAction CancelIfNeeded(in WorkingOrder order) =>
        order.IsActive && order.Status != OrderStatus.PendingCancel ? QuoteAction.Cancel : QuoteAction.None;
}
