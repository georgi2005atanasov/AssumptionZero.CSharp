using System.Diagnostics;
using AssumptionZero.Infrastructure.Simulation;
using AssumptionZero.Domain.InMemory;
using static AssumptionZero.Infrastructure.Simulation.ExchangeProtocol;
namespace AssumptionZero.Simulator;

/// <summary>Independent synthetic exchange accounting; never uses the client's risk ledger.</summary>
public sealed class SimulatedExchange(SimManifest manifest)
{
    private sealed class Order(OrderCommand command, long id, decimal held)
    {
        public OrderCommand Command = command; public long Id = id, Filled; public decimal Held = held;
    }
    private readonly decimal[] free = manifest.InitialFree.Select(Money).ToArray();
    private readonly decimal[] locked = new decimal[manifest.Assets.Length];
    private readonly Dictionary<long, Order> orders = new();
    private readonly PriorityQueue<(long Order, bool Final), (long Due, long Serial)> due = new();
    private readonly HashSet<long> commandIds = new();
    private long nextOrder, sequence, serial, placements;
    public long Sequence => sequence;
    public int OpenOrders => orders.Count;
    public long Accepted { get; private set; }
    public long Rejected { get; private set; }
    public long Fills { get; private set; }
    public long Cancels { get; private set; }
    public decimal Fees { get; private set; }
    public BalanceValue[] Snapshot() => Enumerable.Range(0, free.Length).Select(i => new BalanceValue(i, Money(free[i]), Money(locked[i]))).ToArray();
    private (int Base, int Quote, decimal Price, decimal Quantity) Values(OrderCommand c)
    {
        var i = manifest.Instruments[c.Instrument];
        return (Array.IndexOf(manifest.Assets, i.Symbol[..^4]), Array.IndexOf(manifest.Assets, "USDT"), c.Price * Money(i.Tick), c.Quantity * Money(i.Step));
    }
    private ExecutionEvent Event(OrderCommand c, long id, string kind, OrderStatus status, long filled = 0, string reason = "") =>
        new(++sequence, c.Id, c.Intent, c.Instrument, c.Buy, id, kind, (int)status, filled, c.Price, reason, Changed(c), Stopwatch.GetTimestamp());
    private BalanceValue[] Changed(OrderCommand c)
    {
        var (b, q, _, _) = Values(c);
        return new[] { b, q }.Distinct().Select(i => new BalanceValue(i, Money(free[i]), Money(locked[i]))).ToArray();
    }
    public List<ExecutionEvent> Commands(IEnumerable<OrderCommand> commands, long now)
    {
        var result = new List<ExecutionEvent>();
        foreach (var c in commands)
        {
            if (c.Id <= 0 || !commandIds.Add(c.Id) || c.Instrument < 0 || c.Instrument >= manifest.Instruments.Length)
                throw new InvalidDataException("Duplicate command ID or invalid instrument.");
            if (c.Action == "cancel")
            {
                if (!orders.TryGetValue(c.OrderId, out var old) || old.Command.Instrument != c.Instrument || old.Command.Buy != c.Buy)
                { Rejected++; result.Add(Event(c, c.OrderId, "reject", OrderStatus.Rejected, reason: "unknown_or_terminal_order")); continue; }
                Release(old); orders.Remove(old.Id); Cancels++;
                result.Add(Event(c, old.Id, "execution", OrderStatus.Cancelled, old.Filled)); continue;
            }
            if (c.Action != "place") throw new InvalidDataException("Unsupported command action.");
            var (b, q, p, qty) = Values(c); decimal fee = Money(manifest.Fee);
            decimal hold = c.Buy ? p * qty * (1 + fee) : qty;
            placements++;
            bool scripted = manifest.Scenario == "mixed" && placements % 8 == 0;
            if (c.Price <= 0 || c.Quantity <= 0 || b < 0 || q < 0 || hold <= 0 || free[c.Buy ? q : b] < hold || scripted)
            { Rejected++; result.Add(Event(c, 0, "reject", OrderStatus.Rejected, reason: scripted ? "scenario_rejection" : "invalid_order_or_insufficient_funds")); continue; }
            int asset = c.Buy ? q : b; free[asset] -= hold; locked[asset] += hold;
            var o = new Order(c, ++nextOrder, hold); orders.Add(o.Id, o); Accepted++;
            result.Add(Event(c, o.Id, "ack", OrderStatus.New));
            // Every fourth order rests until cancelled; the others exercise full/partial fills.
            if (manifest.Scenario != "rest" && placements % 4 != 0)
            {
                if (c.Quantity > 1 && placements % 2 == 0) due.Enqueue((o.Id, false), (now + 2, ++serial));
                due.Enqueue((o.Id, true), (now + 4, ++serial));
            }
        }
        return result;
    }
    private void Release(Order o)
    {
        var (b, q, _, _) = Values(o.Command); int asset = o.Command.Buy ? q : b;
        locked[asset] -= o.Held; free[asset] += o.Held; o.Held = 0;
    }
    public List<ExecutionEvent> Advance(long now)
    {
        var result = new List<ExecutionEvent>();
        while (due.TryPeek(out _, out var priority) && priority.Due <= now)
        {
            var item = due.Dequeue(); if (!orders.TryGetValue(item.Order, out var o)) continue;
            long target = item.Final ? o.Command.Quantity : Math.Max(1, o.Command.Quantity / 2);
            long delta = target - o.Filled; if (delta <= 0) continue;
            var (b, q, p, _) = Values(o.Command); decimal quantity = delta * Money(manifest.Instruments[o.Command.Instrument].Step);
            decimal notional = p * quantity, fee = notional * Money(manifest.Fee); Fees += fee;
            decimal spend = o.Command.Buy ? notional + fee : quantity;
            locked[o.Command.Buy ? q : b] -= spend; o.Held -= spend;
            free[o.Command.Buy ? b : q] += o.Command.Buy ? quantity : notional - fee;
            o.Filled = target; Fills++;
            if (item.Final) { Release(o); orders.Remove(o.Id); }
            result.Add(Event(o.Command, o.Id, "execution", item.Final ? OrderStatus.Filled : OrderStatus.PartiallyFilled, o.Filled));
        }
        if (free.Any(x => x < 0) || locked.Any(x => x < 0)) throw new InvalidOperationException("Exchange balance invariant failed.");
        return result;
    }
    public List<ExecutionEvent> Finish()
    {
        var result = Advance(long.MaxValue);
        foreach (var o in orders.Values.OrderBy(x => x.Id).ToArray())
        { Release(o); orders.Remove(o.Id); Cancels++; result.Add(Event(o.Command, o.Id, "execution", OrderStatus.Cancelled, o.Filled)); }
        if (locked.Any(x => x != 0)) throw new InvalidOperationException("Exchange holds remain after drain.");
        return result;
    }
}
