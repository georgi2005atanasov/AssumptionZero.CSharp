using AssumptionZero.Application.Interfaces.Services;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.InMemory;
using static AssumptionZero.Infrastructure.Simulation.ExchangeProtocol;

namespace AssumptionZero.Infrastructure.Simulation;

/// <summary>Only the state-owner loop calls this adapter. No socket task mutates trading state.</summary>
public sealed class SimulationTrader(AccountRiskLedger ledger, IInstrumentRuntime[] runtimes)
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

    private void Trace(string line) => trace.AppendData(Encoding.UTF8.GetBytes(line + "\n"));

    public string TraceHash() => Convert.ToHexString(trace.GetCurrentHash()).ToLowerInvariant();

    private void Submit(int index, bool buy, string action, long order, long price, long quantity, long intent)
    {
        var command = new OrderCommand(++nextCommand, intent, index, buy, action, order, price, quantity,
            LastMarketTime[index]);
        if (action == "cancel")
        {
            cancels.Add(command.Id, command);
        }

        if (Measure)
        {
            commandTimes[command.Id] = Stopwatch.GetTimestamp();
        }

        Outbox.Add(command);
        Commands++;
        Trace($"C|{command.Id}|{command.Intent}|{index}|{(buy ? 1 : 0)}|{action}|{order}|{price}|{quantity}");
    }

    public void Decide(int index)
    {
        var runtime = runtimes[index];
        Decisions++;
        if (runtime.Ready)
        {
            ReadyDecisions++;
        }

        foreach (bool buy in new[] { true, false })
        {
            var decision = runtime.Evaluate();
            var action = buy ? decision.BidAction : decision.AskAction;
            if (pending.ContainsKey((index, buy)) ||
                action is not (QuoteAction.Place or QuoteAction.Replace or QuoteAction.Cancel))
            {
                continue;
            }

            if (!runtime.TryAcceptIntent(buy, decision, out long intent))
            {
                continue;
            }

            var order = buy ? runtime.WorkingOrders.BidOrder : runtime.WorkingOrders.AskOrder;
            long price = buy ? decision.BidPriceTicks : decision.AskPriceTicks,
                quantity = buy ? decision.BidQuantity : decision.AskQuantity;
            pending.Add((index, buy), new(intent, action, price, quantity));
            Submit(
                index,
                buy,
                action == QuoteAction.Place ? "place" : "cancel",
                order.OrderId,
                price,
                quantity,
                intent);
        }
    }

    public void Apply(ExecutionEvent execution, bool finishing = false)
    {
        string fingerprint = Fingerprint(execution);
        if (execution.Sequence == lastSequence && fingerprint == previousEvent)
        {
            return;
        }

        ValidateSequenceAndInstrument(execution);
        IInstrumentRuntime runtime = runtimes[execution.Instrument];
        var key = (execution.Instrument, execution.Buy);
        pending.TryGetValue(key, out Pending? intent);
        if (execution.Kind == "reject")
        {
            ApplyRejection(execution, runtime, intent);
        }
        else
        {
            ApplyExecution(execution, runtime, intent, finishing);
        }

        ApplyBalances(execution.Balances);
        if (!ledger.Ready)
        {
            throw new InvalidDataException("Account reconciliation failed.");
        }

        if (commandTimes.Remove(execution.CommandId, out long created) && Measure)
        {
            ResponseTimes.Add(Stopwatch.GetTimestamp() - created);
        }

        lastSequence = execution.Sequence;
        previousEvent = fingerprint;
        TraceExecution(execution);
    }

    // A single interpolation preserves the existing event-path allocation pattern.

    private static string Fingerprint(ExecutionEvent execution) =>
        $"{execution.Sequence}|{execution.CommandId}|{execution.Intent}|{execution.Instrument}|{execution.Buy}|{execution.OrderId}|{execution.Kind}|{execution.Status}|{execution.Filled}|{execution.Price}|{execution.Reason}|" +
        string.Join(';', execution.Balances.Select(balance => $"{balance.Asset}:{balance.Free}:{balance.Locked}"));

    private void ValidateSequenceAndInstrument(ExecutionEvent execution)
    {
        if (execution.Sequence != lastSequence + 1 || execution.Instrument < 0 ||
            execution.Instrument >= runtimes.Length)
        {
            throw new InvalidDataException("Execution sequence or instrument mismatch.");
        }
    }

    private void ApplyRejection(ExecutionEvent execution, IInstrumentRuntime runtime, Pending? intent)
    {
        WorkingOrder current = execution.Buy ? runtime.WorkingOrders.BidOrder : runtime.WorkingOrders.AskOrder;
        bool terminalCancel = cancels.Remove(execution.CommandId, out OrderCommand? cancel) &&
            cancel.Intent == execution.Intent && cancel.OrderId == execution.OrderId && (!current.IsActive ||
            current.OrderId != execution.OrderId);
        if (terminalCancel)
        {
            return;
        }

        if (intent is null || intent.Intent != execution.Intent)
        {
            throw new InvalidDataException("Unmatched rejection.");
        }

        runtime.RejectIntent(execution.Buy, execution.Intent);
        pending.Remove((execution.Instrument, execution.Buy));
    }

    private void ApplyExecution(ExecutionEvent execution, IInstrumentRuntime runtime, Pending? intent, bool finishing)
    {
        if (execution.Kind == "ack")
        {
            AcknowledgeIntent(execution, runtime, intent);
        }

        ValidateExecution(execution, runtime);
        if (execution.Status == (int)OrderStatus.Cancelled)
        {
            cancels.Remove(execution.CommandId);
        }

        if (execution.Status is < 0 or > 7)
        {
            throw new InvalidDataException("Invalid execution status.");
        }

        runtime.OnExecution(
            execution.Buy,
            execution.OrderId,
            execution.Filled,
            execution.Price,
            LastMarketTime[execution.Instrument],
            (OrderStatus)execution.Status);
        if (execution.Status == (int)OrderStatus.Cancelled && intent?.Action == QuoteAction.Replace)
        {
            ApplyReplacement(execution, runtime, intent, finishing);
        }
        else if (execution.Status is (int)OrderStatus.Cancelled or (int)OrderStatus.Filled or (int)OrderStatus.Expired)
        {
            pending.Remove((execution.Instrument, execution.Buy));
        }
    }

    private void AcknowledgeIntent(ExecutionEvent execution, IInstrumentRuntime runtime, Pending? intent)
    {
        if (intent is null || intent.Intent != execution.Intent || !runtime.AcknowledgeOrderId(execution.Buy,
            execution.Intent, execution.OrderId))
        {
            throw new InvalidDataException("Unmatched acknowledgement.");
        }

        runtime.ConfirmIntent(execution.Buy, execution.Intent);
        pending.Remove((execution.Instrument, execution.Buy));
    }

    private static void ValidateExecution(ExecutionEvent execution, IInstrumentRuntime runtime)
    {
        WorkingOrder before = execution.Buy ? runtime.WorkingOrders.BidOrder : runtime.WorkingOrders.AskOrder;
        if (before.OrderId != execution.OrderId || execution.Filled < before.FilledQuantity ||
            execution.Filled > before.Quantity)
        {
            throw new InvalidDataException("Invalid execution quantities/order ID.");
        }

        if (execution.Kind is not ("ack" or "execution"))
        {
            throw new InvalidDataException("Unknown execution kind.");
        }
    }

    private void ApplyReplacement(ExecutionEvent execution, IInstrumentRuntime runtime, Pending intent, bool finishing)
    {
        var key = (execution.Instrument, execution.Buy);
        if (finishing)
        {
            runtime.RejectIntent(execution.Buy, intent.Intent);
            pending.Remove(key);
        }
        else
        {
            pending[key] = intent with
            {
                Action = QuoteAction.Place
            };
            Submit(execution.Instrument, execution.Buy, "place", 0, intent.Price, intent.Quantity, intent.Intent);
        }
    }

    private void ApplyBalances(BalanceValue[] balances)
    {
        foreach (BalanceValue balance in balances)
        {
            if (balance.Asset < 0 || balance.Asset >= ledger.AssetCount)
            {
                throw new InvalidDataException("Unknown balance asset.");
            }

            ledger.SetBalance(balance.Asset, Money(balance.Free), Money(balance.Locked));
            foreach (IInstrumentRuntime runtime in runtimes)
            {
                runtime.OnBalanceChanged(balance.Asset, confirmPending: false);
            }
        }
    }

    private void TraceExecution(ExecutionEvent execution)
    {
        Trace($"E|{execution.Sequence}|{execution.CommandId}|{execution.Intent}|{execution.Instrument}|{(execution.Buy ? 1 : 0)}|{execution.OrderId}|{execution.Kind}|{execution.Status}|{execution.Filled}|{execution.Price}|{execution.Reason}");
        foreach (BalanceValue balance in execution.Balances)
        {
            Trace($"B|{balance.Asset}|{balance.Free}|{balance.Locked}");
        }
    }

    public object Snapshot() => new
    {
        balances = ledger.Assets.Select((asset, index) => new { asset, free = Money(ledger.RawFree(index)),
            locked = Money(ledger.Locked(index)), reserved = Money(ledger.Reserved(index)) }).ToArray(),
        pending = ledger.PendingCount,
        instruments = runtimes.Select(SnapshotInstrument).ToArray()
    };

    private static object SnapshotInstrument(IInstrumentRuntime runtime) => new
    {
        symbol = runtime.Instrument.Symbol,
        inventory = runtime.Inventory.CurrentQuantity,
        bid = SnapshotOrder(runtime.WorkingOrders.BidOrder),
        ask = SnapshotOrder(runtime.WorkingOrders.AskOrder),
        ready = runtime.Ready,
        book = runtime.OrderBook.IsSynchronized,
        warm = runtime.SignalMemory.Warm,
        faulted = runtime.SignalMemory.Faulted
    };

    private static long[] SnapshotOrder(WorkingOrder order) => [order.OrderId, order.PriceTicks, order.Quantity,
        order.FilledQuantity, (int)order.Status, order.IsActive ? 1 : 0];

    public void AssertSettled()
    {
        if (ledger.PendingCount != 0 || pending.Count != 0 || cancels.Count != 0 ||
            runtimes.Any(r => r.WorkingOrders.BidOrder.IsActive || r.WorkingOrders.AskOrder.IsActive))
        {
            throw new InvalidOperationException("Unsettled client state after final report.");
        }
    }
}
