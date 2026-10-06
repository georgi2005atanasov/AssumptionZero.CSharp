using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.InMemory;
using static AssumptionZero.Infrastructure.Simulation.ExchangeProtocol;
namespace AssumptionZero.Infrastructure.Simulation;

/// <summary>Only the state-owner loop calls this adapter. No socket task mutates trading state.</summary>
public sealed class SimulationTrader(AccountRiskLedger ledger, InstrumentRuntime[] runtimes)
{
    private sealed record Pending(long Intent, QuoteAction Action, long Price, long Quantity);
    private readonly Dictionary<(int, bool), Pending> pending = new();
    private readonly Dictionary<long, OrderCommand> cancels = new();
    public bool Measure { get; set; }
    private readonly Dictionary<long, long> commandTimes = new();
    public List<long> ResponseTimes { get; } = new();
    private long nextCommand;
    private long lastSequence;
    private string? previousEvent;
    public long Commands { get; private set; }
    public long Reports => lastSequence;
    public long Decisions { get; private set; }
    public long ReadyDecisions { get; private set; }
    public List<OrderCommand> Outbox { get; } = new();
    public long[] LastMarketTime { get; } = new long[runtimes.Length];
    private readonly IncrementalHash trace = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private void Trace(string s) => trace.AppendData(Encoding.UTF8.GetBytes(s + "\n"));
    public string TraceHash() => Convert.ToHexString(trace.GetCurrentHash()).ToLowerInvariant();
    private void Submit(int index, bool buy, string action, long order, long price, long quantity, long intent)
    {
        var c = new OrderCommand(++nextCommand, intent, index, buy, action, order, price, quantity, LastMarketTime[index]);
        if (action == "cancel") cancels.Add(c.Id, c);
        if (Measure) commandTimes[c.Id] = Stopwatch.GetTimestamp();
        Outbox.Add(c); Commands++; Trace($"C|{c.Id}|{c.Intent}|{index}|{(buy ? 1 : 0)}|{action}|{order}|{price}|{quantity}");
    }
    public void Decide(int index)
    {
        var r = runtimes[index]; Decisions++; if (r.Ready) ReadyDecisions++;
        foreach (bool buy in new[] { true, false })
        {
            var d = r.Evaluate(); var action = buy ? d.BidAction : d.AskAction;
            if (pending.ContainsKey((index, buy)) || action is not (QuoteAction.Place or QuoteAction.Replace or QuoteAction.Cancel)) continue;
            if (!r.TryAcceptIntent(buy, d, out long intent)) continue;
            var order = buy ? r.WorkingOrders.BidOrder : r.WorkingOrders.AskOrder;
            long price = buy ? d.BidPriceTicks : d.AskPriceTicks, quantity = buy ? d.BidQuantity : d.AskQuantity;
            pending.Add((index, buy), new(intent, action, price, quantity));
            Submit(index, buy, action == QuoteAction.Place ? "place" : "cancel", order.OrderId, price, quantity, intent);
        }
    }
    public void Apply(ExecutionEvent e, bool finishing = false)
    {
        string fingerprint = $"{e.Sequence}|{e.CommandId}|{e.Intent}|{e.Instrument}|{e.Buy}|{e.OrderId}|{e.Kind}|{e.Status}|{e.Filled}|{e.Price}|{e.Reason}|" + string.Join(';', e.Balances.Select(b => $"{b.Asset}:{b.Free}:{b.Locked}"));
        if (e.Sequence == lastSequence && fingerprint == previousEvent) return;
        if (e.Sequence != lastSequence + 1 || e.Instrument < 0 || e.Instrument >= runtimes.Length) throw new InvalidDataException("Execution sequence or instrument mismatch.");
        var r = runtimes[e.Instrument]; var key = (e.Instrument, e.Buy); pending.TryGetValue(key, out var p);
        if (e.Kind == "reject")
        {
            var current = e.Buy ? r.WorkingOrders.BidOrder : r.WorkingOrders.AskOrder;
            bool terminalCancel = cancels.Remove(e.CommandId, out var cancel) && cancel.Intent == e.Intent && cancel.OrderId == e.OrderId && (!current.IsActive || current.OrderId != e.OrderId);
            if (!terminalCancel)
            {
                if (p is null || p.Intent != e.Intent) throw new InvalidDataException("Unmatched rejection.");
                r.RejectIntent(e.Buy, e.Intent); pending.Remove(key);
            }
        }
        else
        {
            if (e.Kind == "ack")
            {
                if (p is null || p.Intent != e.Intent || !r.AcknowledgeOrderId(e.Buy, e.Intent, e.OrderId)) throw new InvalidDataException("Unmatched acknowledgement.");
                r.ConfirmIntent(e.Buy, e.Intent); pending.Remove(key);
            }
            var before = e.Buy ? r.WorkingOrders.BidOrder : r.WorkingOrders.AskOrder;
            if (before.OrderId != e.OrderId || e.Filled < before.FilledQuantity || e.Filled > before.Quantity) throw new InvalidDataException("Invalid execution quantities/order ID.");
            if (e.Kind is not ("ack" or "execution")) throw new InvalidDataException("Unknown execution kind.");
            if (e.Status == (int)OrderStatus.Cancelled) cancels.Remove(e.CommandId);
            if (e.Status is < 0 or > 7) throw new InvalidDataException("Invalid execution status.");
            r.OnExecution(e.Buy, e.OrderId, e.Filled, e.Price, LastMarketTime[e.Instrument], (OrderStatus)e.Status);
            if (e.Status == (int)OrderStatus.Cancelled && p?.Action == QuoteAction.Replace)
            {
                if (finishing) { r.RejectIntent(e.Buy, p.Intent); pending.Remove(key); }
                else { pending[key] = p with { Action = QuoteAction.Place }; Submit(e.Instrument, e.Buy, "place", 0, p.Price, p.Quantity, p.Intent); }
            }
            else if (e.Status is (int)OrderStatus.Cancelled or (int)OrderStatus.Filled or (int)OrderStatus.Expired) pending.Remove(key);
        }
        foreach (var b in e.Balances)
        {
            if (b.Asset < 0 || b.Asset >= ledger.AssetCount) throw new InvalidDataException("Unknown balance asset.");
            ledger.SetBalance(b.Asset, Money(b.Free), Money(b.Locked));
            foreach (var runtime in runtimes) runtime.OnBalanceChanged(b.Asset, confirmPending: false);
        }
        if (!ledger.Ready) throw new InvalidDataException("Account reconciliation failed.");
        if (commandTimes.Remove(e.CommandId, out long created) && Measure) ResponseTimes.Add(Stopwatch.GetTimestamp() - created);
        lastSequence = e.Sequence; previousEvent = fingerprint;
        Trace($"E|{e.Sequence}|{e.CommandId}|{e.Intent}|{e.Instrument}|{(e.Buy ? 1 : 0)}|{e.OrderId}|{e.Kind}|{e.Status}|{e.Filled}|{e.Price}|{e.Reason}");
        foreach (var b in e.Balances) Trace($"B|{b.Asset}|{b.Free}|{b.Locked}");
    }
    public object Snapshot() => new
    {
        balances = ledger.Assets.Select((a, i) => new { asset = a, free = Money(ledger.RawFree(i)), locked = Money(ledger.Locked(i)), reserved = Money(ledger.Reserved(i)) }).ToArray(),
        pending = ledger.PendingCount,
        instruments = runtimes.Select(r => new
        {
            symbol = r.Instrument.Symbol,
            inventory = r.Inventory.CurrentQuantity,
            bid = new long[] { r.WorkingOrders.BidOrder.OrderId, r.WorkingOrders.BidOrder.PriceTicks, r.WorkingOrders.BidOrder.Quantity, r.WorkingOrders.BidOrder.FilledQuantity, (int)r.WorkingOrders.BidOrder.Status, r.WorkingOrders.BidOrder.IsActive ? 1 : 0 },
            ask = new long[] { r.WorkingOrders.AskOrder.OrderId, r.WorkingOrders.AskOrder.PriceTicks, r.WorkingOrders.AskOrder.Quantity, r.WorkingOrders.AskOrder.FilledQuantity, (int)r.WorkingOrders.AskOrder.Status, r.WorkingOrders.AskOrder.IsActive ? 1 : 0 },
            ready = r.Ready,
            book = r.OrderBook.IsSynchronized,
            warm = r.SignalMemory.Warm,
            faulted = r.SignalMemory.Faulted
        }).ToArray()
    };
    public void AssertSettled()
    {
        if (ledger.PendingCount != 0 || pending.Count != 0 || cancels.Count != 0 || runtimes.Any(r => r.WorkingOrders.BidOrder.IsActive || r.WorkingOrders.AskOrder.IsActive)) throw new InvalidOperationException("Unsettled client state after final report.");
    }
}
