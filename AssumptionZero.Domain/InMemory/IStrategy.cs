using AssumptionZero.Domain.Entities;

namespace AssumptionZero.Domain.InMemory;

public interface IStrategy
{
    QuoteDecision Evaluate(
        Instrument instrument,
        in HotConfig config,
        in OrderBook book,
        in SignalState signals,
        in InventoryState inventory,
        in WorkingOrders orders,
        ref PricingState pricing,
        double adverseScore,
        decimal makerFeeRate,
        decimal freeBase,
        decimal freeQuote
    );
}