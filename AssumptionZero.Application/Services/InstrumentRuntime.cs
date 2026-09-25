using AssumptionZero.Application.Interfaces.Services;
using AssumptionZero.Domain.Entities;
using AssumptionZero.Domain.InMemory;

namespace AssumptionZero.Application.Services;

public sealed class InstrumentRuntime : IInstrumentRuntime
{
    private OrderBook book;
    private SignalState signals;
    private SignalMemory memory;
    private PricingState pricing;
    private InventoryState inventory;
    private WorkingOrders orders;
    private readonly AccountRiskLedger account;
    private readonly HotConfig config;
    private readonly int baseAsset;
    private readonly int quoteAsset;
    private long pendingLastId;
    private long priorBid, priorAsk, priorBidQuantity, priorAskQuantity;
    private long lastEventNs;
    private bool diffOpen;
    private double adverseScore;
    private long pendingBidIntent, pendingAskIntent;
    private long pendingBidCancelHold, pendingAskCancelHold;
    private QuoteAction pendingBidAction, pendingAskAction;
    private OrderStatus priorBidStatus, priorAskStatus;
    private long replacementBidPrice, replacementAskPrice, replacementBidQuantity, replacementAskQuantity;

    public Instrument Instrument { get; set; }
    public OrderBook OrderBook { get => book; set => book = value; }
    public SignalState Signals { get => signals; set => signals = value; }
    public SignalMemory SignalMemory { get => memory; set => memory = value; }
    public PricingState PricingState { get => pricing; set => pricing = value; }
    public InventoryState Inventory { get => inventory; set => inventory = value; }
    public WorkingOrders WorkingOrders { get => orders; set => orders = value; }
    public IStrategy Strategy { get; set; }
    public bool Ready => account.Ready && book.IsSynchronized && memory.Warm && !memory.Faulted;
    public HotConfig Config => config;

    public InstrumentRuntime(Instrument instrument, in HotConfig config, AccountRiskLedger account,
        long targetQuantity, long maxLongQuantity, long maxShortQuantity = 0, IStrategy? strategy = null)
    {
        Instrument = instrument;
        this.config = config;
        this.account = account;
        baseAsset = account.FindAsset(instrument.BaseAsset);
        quoteAsset = account.FindAsset(instrument.QuoteAsset);
        if (baseAsset < 0 || quoteAsset < 0) throw new ArgumentException("Instrument assets must exist in account ledger.");
        book = new OrderBook(5_000);
        memory = new SignalMemory(config);
        inventory = new InventoryState
        {
            TargetQuantity = targetQuantity,
            MaxLongQuantity = maxLongQuantity,
            MaxShortQuantity = maxShortQuantity
        };
        Strategy = strategy ?? new MarketMakingStrategy();
    }

    public bool LoadSnapshot(ReadOnlySpan<BookLevel> bids, ReadOnlySpan<BookLevel> asks, long updateId)
    {
        memory.Reset();
        lastEventNs = 0;
        return book.LoadSnapshot(bids, asks, updateId) &&
               book.BidCount >= config.BookLevels && book.AskCount >= config.BookLevels;
    }

    public bool BeginDepth(long firstId, long lastId)
    {
        diffOpen = false;
        if (!book.CanApply(firstId, lastId)) { book.Invalidate(); return false; }
        if (lastId <= book.LastUpdateId) return false;
        priorBid = book.Bids[0].PriceTicks;
        priorAsk = book.Asks[0].PriceTicks;
        priorBidQuantity = book.Bids[0].Quantity;
        priorAskQuantity = book.Asks[0].Quantity;
        pendingLastId = lastId;
        diffOpen = true;
        return true;
    }

    public bool ApplyBid(long priceTicks, long quantity)
    {
        if (!diffOpen || !book.ApplyBid(priceTicks, quantity)) { Invalidate(); return false; }
        return true;
    }

    public bool ApplyAsk(long priceTicks, long quantity)
    {
        if (!diffOpen || !book.ApplyAsk(priceTicks, quantity)) { Invalidate(); return false; }
        return true;
    }

    public QuoteDecision EndDepth(long eventTimeNs)
    {
        if (!diffOpen || !book.CompleteDiff(pendingLastId, config.BookLevels))
        {
            Invalidate();
            return InvalidDecision();
        }
        diffOpen = false;
        long time = Math.Max(lastEventNs, eventTimeNs);
        lastEventNs = time;
        double bidOfi = book.Bids[0].PriceTicks > priorBid ? book.Bids[0].Quantity :
            book.Bids[0].PriceTicks < priorBid ? -priorBidQuantity : book.Bids[0].Quantity - priorBidQuantity;
        double askOfi = book.Asks[0].PriceTicks < priorAsk ? -book.Asks[0].Quantity :
            book.Asks[0].PriceTicks > priorAsk ? priorAskQuantity : priorAskQuantity - book.Asks[0].Quantity;
        memory.AddOfi(bidOfi + askOfi);
        adverseScore = memory.UpdateBook(time, book, config, ref signals);
        return Evaluate();
    }

    public QuoteDecision OnMarketTrade(long eventTimeNs, double quantity, bool aggressiveBuy)
    {
        long time = Math.Max(lastEventNs, eventTimeNs);
        lastEventNs = time;
        memory.AddTrade(time, quantity, aggressiveBuy, config);
        memory.UpdateTradeFlow(time, config, ref signals);
        return Evaluate();
    }

    public void OnBalanceChanged(int changedAsset = -1)
    {
        inventory.CurrentQuantity = Instrument.ToQuantityUnits(account.Total(baseAsset));
        if (changedAsset == quoteAsset && pendingBidIntent > 0 && orders.BidOrder.OrderId > 0 &&
            pendingBidAction == QuoteAction.Place) ClearIntent(true);
        if (changedAsset == baseAsset && pendingAskIntent > 0 && orders.AskOrder.OrderId > 0 &&
            pendingAskAction == QuoteAction.Place) ClearIntent(false);
    }

    public void ConfigureInventory(long targetQuantity, long maxLongQuantity, long maxShortQuantity = 0)
    {
        if (maxLongQuantity < targetQuantity || maxShortQuantity < 0 || targetQuantity < -maxShortQuantity)
            throw new ArgumentOutOfRangeException(nameof(targetQuantity));
        inventory.TargetQuantity = targetQuantity;
        inventory.MaxLongQuantity = maxLongQuantity;
        inventory.MaxShortQuantity = maxShortQuantity;
    }

    public void SetWorkingOrders(in WorkingOrders current)
    {
        orders = current;
        RecalculateOutstanding();
    }

    public void BeginAccountReconciliation()
    {
        pendingBidIntent = pendingAskIntent = 0;
        pendingBidCancelHold = pendingAskCancelHold = 0;
        pendingBidAction = pendingAskAction = QuoteAction.None;
        orders = default;
        RecalculateOutstanding();
    }

    public void OnExecution(bool buy, long orderId, long cumulativeFilled, long fillPriceTicks, long eventTimeNs,
        OrderStatus status)
    {
        ref WorkingOrder order = ref (buy ? ref orders.BidOrder : ref orders.AskOrder);
        if (order.OrderId != orderId || cumulativeFilled < order.FilledQuantity || cumulativeFilled > order.Quantity)
        {
            Invalidate();
            return;
        }
        long delta = cumulativeFilled - order.FilledQuantity;
        order.FilledQuantity = cumulativeFilled;
        order.Status = status;
        order.IsActive = status is OrderStatus.PendingNew or OrderStatus.PendingCancel or
            OrderStatus.New or OrderStatus.PartiallyFilled;
        if (!order.IsActive)
        {
            long cancelHold = buy ? pendingBidCancelHold : pendingAskCancelHold;
            if (cancelHold > 0) account.Release(cancelHold);
            if (buy) pendingBidCancelHold = 0; else pendingAskCancelHold = 0;
            QuoteAction pending = buy ? pendingBidAction : pendingAskAction;
            if (pending == QuoteAction.Cancel || pending == QuoteAction.Replace && status != OrderStatus.Cancelled)
                ClearIntent(buy);
        }
        if (!order.IsActive && status == OrderStatus.Cancelled &&
            (buy ? pendingBidAction : pendingAskAction) == QuoteAction.Replace)
        {
            order = new WorkingOrder
            {
                PriceTicks = buy ? replacementBidPrice : replacementAskPrice,
                Quantity = buy ? replacementBidQuantity : replacementAskQuantity,
                Status = OrderStatus.PendingNew,
                IsActive = true
            };
            if (buy) pendingBidAction = QuoteAction.Place; else pendingAskAction = QuoteAction.Place;
        }
        if (delta > 0)
        {
            inventory.CurrentQuantity += buy ? delta : -delta;
            memory.AddFill(eventTimeNs, fillPriceTicks, buy);
        }
        RecalculateOutstanding();
    }

    public QuoteDecision Evaluate()
    {
        if (!Ready) return InvalidDecision();
        return Strategy.Evaluate(Instrument, config, book, signals, inventory, orders, ref pricing,
            adverseScore, account.MakerFeeRate, account.Free(baseAsset), account.Free(quoteAsset));
    }

    /// <summary>Reserve shared funds only after a downstream consumer accepts a current intent.</summary>
    public bool TryAcceptIntent(bool buy, in QuoteDecision decision, out long intentId)
    {
        intentId = 0;
        if ((buy ? pendingBidAction : pendingAskAction) != QuoteAction.None) return false;
        QuoteDecision current = Evaluate();
        if (current != decision) return false;
        QuoteAction action = buy ? current.BidAction : current.AskAction;
        if (action is not (QuoteAction.Place or QuoteAction.Replace or QuoteAction.Cancel) ||
            (action != QuoteAction.Cancel && !Ready)) return false;
        ref WorkingOrder order = ref (buy ? ref orders.BidOrder : ref orders.AskOrder);
        if (buy) { priorBidStatus = order.Status; pendingBidAction = action; }
        else { priorAskStatus = order.Status; pendingAskAction = action; }
        if (action == QuoteAction.Cancel)
        {
            if (!account.ReservePendingCancel(Instrument, buy, order.PriceTicks,
                    Math.Max(0, order.Quantity - order.FilledQuantity), out long holdId))
            {
                if (buy) pendingBidAction = QuoteAction.None; else pendingAskAction = QuoteAction.None;
                return false;
            }
            if (buy) pendingBidCancelHold = holdId; else pendingAskCancelHold = holdId;
            order.Status = OrderStatus.PendingCancel;
            intentId = holdId;
            return true;
        }
        long price = buy ? current.BidPriceTicks : current.AskPriceTicks;
        long quantity = buy ? current.BidQuantity : current.AskQuantity;
        if (!account.TryAccept(Instrument, buy, price, quantity, out intentId))
        {
            if (buy) pendingBidAction = QuoteAction.None; else pendingAskAction = QuoteAction.None;
            return false;
        }
        if (buy) pendingBidIntent = intentId; else pendingAskIntent = intentId;
        if (action == QuoteAction.Replace)
        {
            if (!account.ReservePendingCancel(Instrument, buy, order.PriceTicks,
                    Math.Max(0, order.Quantity - order.FilledQuantity), out long holdId))
            {
                ClearIntent(buy);
                intentId = 0;
                return false;
            }
            if (buy) pendingBidCancelHold = holdId; else pendingAskCancelHold = holdId;
        }
        if (action == QuoteAction.Place)
            order = new WorkingOrder { PriceTicks = price, Quantity = quantity,
                Status = OrderStatus.PendingNew, IsActive = true };
        else
        {
            order.Status = OrderStatus.PendingCancel;
            if (buy) { replacementBidPrice = price; replacementBidQuantity = quantity; }
            else { replacementAskPrice = price; replacementAskQuantity = quantity; }
        }
        RecalculateOutstanding();
        return true;
    }

    public void RejectIntent(bool buy, long intentId)
    {
        QuoteAction action = buy ? pendingBidAction : pendingAskAction;
        long expected = action == QuoteAction.Cancel ?
            (buy ? pendingBidCancelHold : pendingAskCancelHold) :
            (buy ? pendingBidIntent : pendingAskIntent);
        if (action == QuoteAction.None || expected != intentId) return;
        ref WorkingOrder order = ref (buy ? ref orders.BidOrder : ref orders.AskOrder);
        if (action == QuoteAction.Place) order = default;
        else order.Status = buy ? priorBidStatus : priorAskStatus;
        ClearIntent(buy);
        long cancelHold = buy ? pendingBidCancelHold : pendingAskCancelHold;
        if (cancelHold > 0) account.Release(cancelHold);
        if (buy) pendingBidCancelHold = 0; else pendingAskCancelHold = 0;
        RecalculateOutstanding();
    }

    /// <summary>Bind an accepted place or replacement to the exchange order ID before execution events are trusted.</summary>
    public bool AcknowledgeOrderId(bool buy, long intentId, long orderId)
    {
        if (orderId <= 0 || (buy ? pendingBidIntent : pendingAskIntent) != intentId ||
            (buy ? pendingBidAction : pendingAskAction) != QuoteAction.Place) return false;
        ref WorkingOrder order = ref (buy ? ref orders.BidOrder : ref orders.AskOrder);
        if (!order.IsActive || order.Status != OrderStatus.PendingNew || order.OrderId != 0) return false;
        order.OrderId = orderId;
        return true;
    }

    private void ClearIntent(bool buy)
    {
        long id = buy ? pendingBidIntent : pendingAskIntent;
        if (id > 0) account.Release(id);
        if (buy) { pendingBidIntent = 0; pendingBidAction = QuoteAction.None; }
        else { pendingAskIntent = 0; pendingAskAction = QuoteAction.None; }
    }

    public void Invalidate()
    {
        diffOpen = false;
        book.Invalidate();
    }

    private void RecalculateOutstanding()
    {
        inventory.ReservedBuyQuantity = orders.BidOrder.IsActive ?
            Math.Max(0, orders.BidOrder.Quantity - orders.BidOrder.FilledQuantity) : 0;
        inventory.ReservedSellQuantity = orders.AskOrder.IsActive ?
            Math.Max(0, orders.AskOrder.Quantity - orders.AskOrder.FilledQuantity) : 0;
    }

    private QuoteDecision InvalidDecision() => new(0, 0,
        orders.BidOrder.IsActive && orders.BidOrder.Status != OrderStatus.PendingCancel ?
            QuoteAction.Cancel : QuoteAction.None,
        0, 0, orders.AskOrder.IsActive && orders.AskOrder.Status != OrderStatus.PendingCancel ?
            QuoteAction.Cancel : QuoteAction.None);
}
