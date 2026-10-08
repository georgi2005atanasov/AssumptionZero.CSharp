using AssumptionZero.Domain.Entities;
using AssumptionZero.Domain.InMemory;

namespace AssumptionZero.Application.Interfaces.Services;

public interface IInstrumentRuntime
{
    Instrument Instrument { get; set; }

    OrderBook OrderBook { get; set; }

    SignalState Signals { get; set; }

    SignalMemory SignalMemory { get; set; }

    PricingState PricingState { get; set; }

    InventoryState Inventory { get; set; }

    WorkingOrders WorkingOrders { get; set; }

    IStrategy Strategy { get; set; }

    bool AccountReady { get; }

    bool Ready { get; }

    bool EstimatesBookFromFeed { get; }

    HotConfig Config { get; }

    bool BenchmarkStages { get; set; }

    long LastSignalTicks { get; }

    long LastQuoteTicks { get; }

    bool LoadSnapshot(ReadOnlySpan<BookLevel> bids, ReadOnlySpan<BookLevel> asks, long updateId);
    void SetEstimatedBookPrecision(sbyte priceExponent, sbyte quantityExponent);
    bool BeginDepth(long firstId, long lastId);
    bool SeedEstimatedBook(long bidPrice, long bidQuantity, long askPrice, long askQuantity, long updateId);
    bool ObserveDepthForEstimatedBook(bool bid, long price, long quantity, long updateId);
    bool ApplyBid(long priceTicks, long quantity);
    bool ApplyAsk(long priceTicks, long quantity);
    QuoteDecision EndDepth(long eventTimeNs);
    QuoteDecision OnMarketTrade(long eventTimeNs, double quantity, bool aggressiveBuy);
    void OnBalanceChanged(int changedAsset = -1, bool confirmPending = true);
    void ConfigureInventory(long targetQuantity, long maxLongQuantity, long maxShortQuantity = 0);
    void SetWorkingOrders(in WorkingOrders current);
    void BeginAccountReconciliation();
    void OnExecution(
        bool buy,
        long orderId,
        long cumulativeFilled,
        long fillPriceTicks,
        long eventTimeNs,
        OrderStatus status);
    QuoteDecision Evaluate();
    bool TryAcceptIntent(bool buy, in QuoteDecision decision, out long intentId);
    void RejectIntent(bool buy, long intentId);
    bool AcknowledgeOrderId(bool buy, long intentId, long orderId);
    void ConfirmIntent(bool buy, long intentId);
    void Invalidate();
}
