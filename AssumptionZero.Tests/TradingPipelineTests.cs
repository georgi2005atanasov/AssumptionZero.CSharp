using System.Diagnostics;
using System.Net.WebSockets;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.Entities;
using AssumptionZero.Domain.InMemory;
using AssumptionZero.Infrastructure;
using AssumptionZero.Infrastructure.External;
using AssumptionZero.Simulator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.SbeTool.Sbe.Dll;
using Spot_stream;

namespace AssumptionZero.Tests;

[TestClass]
public class TradingPipelineTests
{
    private static Instrument Instrument(string symbol = "BTCUSDT") =>
        new(0, symbol, "BTC", "USDT", 1, 1, 1, 100_000, 1);

    private static (InstrumentRuntime Runtime, AccountRiskLedger Ledger) Runtime(
        HotConfig? settings = null)
    {
        HotConfig config = settings ?? new HotConfig(bookLevels: 1, ofiWindow: 1,
            momentumWindowNs: 1_000, baseQuantity: 10);
        var ledger = new AccountRiskLedger(["BTC", "USDT"]);
        ledger.BeginSnapshot(0);
        ledger.SetBalance(0, 100, 0);
        ledger.SetBalance(1, 100_000, 0);
        ledger.CompleteSnapshot();
        var runtime = new InstrumentRuntime(Instrument(), config, ledger, 100, 200);
        runtime.OnBalanceChanged();
        Assert.IsTrue(runtime.LoadSnapshot(
            [new BookLevel { PriceTicks = 100, Quantity = 30 },
             new BookLevel { PriceTicks = 99, Quantity = 20 }],
            [new BookLevel { PriceTicks = 102, Quantity = 10 },
             new BookLevel { PriceTicks = 103, Quantity = 20 }], 9));
        return (runtime, ledger);
    }

    private static byte[] Depth(long updateId, long eventTimeMicros, long bidQuantity = 30)
    {
        var bytes = new byte[256];
        var encoder = new DepthDiffStreamEvent().WrapForEncodeAndApplyHeader(
            new DirectBuffer(bytes), 0, new MessageHeader());
        encoder.EventTime = eventTimeMicros;
        encoder.FirstBookUpdateId = updateId;
        encoder.LastBookUpdateId = updateId;
        encoder.PriceExponent = 0;
        encoder.QtyExponent = 0;
        var bid = encoder.BidsCount(1).Next();
        bid.Price = 100;
        bid.Qty = bidQuantity;
        var ask = encoder.AsksCount(1).Next();
        ask.Price = 102;
        ask.Qty = 10;
        encoder.SetSymbol("BTCUSDT");
        return bytes[..encoder.Limit];
    }

    private static byte[] Trade(long eventTimeMicros)
    {
        var bytes = new byte[256];
        var encoder = new TradesStreamEvent().WrapForEncodeAndApplyHeader(
            new DirectBuffer(bytes), 0, new MessageHeader());
        encoder.EventTime = eventTimeMicros;
        encoder.PriceExponent = 0;
        encoder.QtyExponent = -1;
        var trade = encoder.TradesCount(1).Next();
        trade.Id = 1;
        trade.Price = 101;
        trade.Qty = 5; // 0.5: valid trade flow even though order step is 1.
        trade.IsBuyerMaker = BoolEnum.False;
        encoder.SetSymbol("BTCUSDT");
        return bytes[..encoder.Limit];
    }

    [TestMethod]
    public void SignalsMatchHandCalculatedDepthFlowAndMarkout()
    {
        var (runtime, _) = Runtime(new HotConfig(bookLevels: 2, ofiWindow: 1,
            momentumWindowNs: 1_000_000_000, adverseScaleTicks: 5));
        SignalMemory memory = runtime.SignalMemory;
        SignalState state = default;
        HotConfig config = runtime.Config;
        memory.AddOfi(5);
        memory.AddTrade(0, 10, true, config);
        memory.AddTrade(0, 5, false, config);
        double adverse = memory.UpdateBook(0, runtime.OrderBook, config, ref state);
        Assert.AreEqual(0.5, state.MicroSignal, 1e-12);
        Assert.AreEqual(1.0 / 3, state.MultiLevelImbalance, 1e-12);
        Assert.AreEqual(1.0, state.OrderFlowImbalance, 1e-12);
        Assert.AreEqual(1.0 / 3, state.TradeFlowImbalance, 1e-12);
        Assert.AreEqual(1.0, state.LiquidityRatio, 1e-12);
        Assert.AreEqual(0, adverse, 1e-12);

        memory.AddFill(0, 101, true);
        OrderBook book = runtime.OrderBook;
        Assert.IsTrue(book.ApplyBid(100, 0));
        Assert.IsTrue(book.ApplyBid(99, 0));
        Assert.IsTrue(book.ApplyBid(98, 10));
        Assert.IsTrue(book.ApplyBid(97, 10));
        Assert.IsTrue(book.ApplyAsk(102, 0));
        Assert.IsTrue(book.ApplyAsk(100, 10));
        Assert.IsTrue(book.CompleteDiff(10, 2));
        adverse = memory.UpdateBook(250_000_000, book, config, ref state);
        Assert.AreEqual(0.4, adverse, 1e-12);
    }

    [TestMethod]
    public void QuoteFormulaAndExchangeFiltersMatchHandCalculation()
    {
        var (runtime, _) = Runtime(new HotConfig(bookLevels: 1, ofiWindow: 1,
            microWeight: 1, mliWeight: 0, ofiWeight: 0, tradeFlowWeight: 0,
            momentumWeight: 0, maxFairShiftTicks: 2, baseHalfSpreadTicks: 2,
            volatilitySpreadWeightTicks: 0, liquiditySpreadWeightTicks: 0,
            adverseSpreadWeightTicks: 0, inventorySpreadWeightTicks: 0,
            baseQuantity: 10));
        var signals = new SignalState { MicroSignal = 0.5, LiquidityRatio = 1 };
        var pricing = new PricingState();
        QuoteDecision decision = runtime.Strategy.Evaluate(runtime.Instrument, runtime.Config,
            runtime.OrderBook, signals, runtime.Inventory, runtime.WorkingOrders, ref pricing,
            0, 0, 100, 100_000);
        Assert.AreEqual(100L, decision.BidPriceTicks);
        Assert.AreEqual(104L, decision.AskPriceTicks);
        Assert.AreEqual(10L, decision.BidQuantity);
        Assert.AreEqual(10L, decision.AskQuantity);
        Assert.AreEqual(QuoteAction.Place, decision.BidAction);
        Assert.AreEqual(QuoteAction.Place, decision.AskAction);

        var orders = new WorkingOrders
        {
            BidOrder = new WorkingOrder { OrderId = 1, PriceTicks = 100, Quantity = 10,
                Status = OrderStatus.New, IsActive = true },
            AskOrder = new WorkingOrder { OrderId = 2, PriceTicks = 104, Quantity = 10,
                Status = OrderStatus.New, IsActive = true }
        };
        decision = runtime.Strategy.Evaluate(runtime.Instrument, runtime.Config,
            runtime.OrderBook, signals, runtime.Inventory, orders, ref pricing,
            0, 0, 100, 100_000);
        Assert.AreEqual(QuoteAction.Keep, decision.BidAction);
        Assert.AreEqual(QuoteAction.Keep, decision.AskAction);
        orders.BidOrder.Quantity = 9;
        decision = runtime.Strategy.Evaluate(runtime.Instrument, runtime.Config,
            runtime.OrderBook, signals, runtime.Inventory, orders, ref pricing,
            0, 0, 100, 100_000);
        Assert.AreEqual(QuoteAction.Replace, decision.BidAction);

        var filtered = new Instrument(0, "BTCUSDT", "BTC", "USDT", 1, 1, 20, 100_000, 1);
        decision = runtime.Strategy.Evaluate(filtered, runtime.Config, runtime.OrderBook,
            signals, runtime.Inventory, runtime.WorkingOrders, ref pricing, 0, 0, 100, 100_000);
        Assert.AreEqual(QuoteAction.None, decision.BidAction);
        Assert.AreEqual(QuoteAction.None, decision.AskAction);

        var priceFiltered = new Instrument(0, "BTCUSDT", "BTC", "USDT", 1, 1, 1, 100_000, 1,
            minPrice: 1, maxPrice: 103);
        decision = runtime.Strategy.Evaluate(priceFiltered, runtime.Config, runtime.OrderBook,
            signals, runtime.Inventory, runtime.WorkingOrders, ref pricing, 0, 0, 100, 100_000);
        Assert.AreEqual(QuoteAction.Place, decision.BidAction);
        Assert.AreEqual(QuoteAction.None, decision.AskAction);
    }

    [TestMethod]
    public void GapsOverflowPartialFillsAndPendingCancelsFailClosed()
    {
        var (runtime, _) = Runtime();
        Assert.IsFalse(runtime.OrderBook.CanApply(11, 11));
        Assert.IsTrue(runtime.BeginDepth(10, 10));
        runtime.EndDepth(0);
        Assert.IsTrue(runtime.BeginDepth(11, 11));
        runtime.EndDepth(1_000);
        Assert.IsTrue(runtime.Ready);

        var working = new WorkingOrders { BidOrder = new WorkingOrder
        {
            OrderId = 42, PriceTicks = 100, Quantity = 10,
            Status = OrderStatus.New, IsActive = true
        } };
        runtime.SetWorkingOrders(working);
        runtime.OnExecution(true, 42, 3, 100, 2_000, OrderStatus.PartiallyFilled);
        Assert.AreEqual(103L, runtime.Inventory.CurrentQuantity);
        Assert.AreEqual(7L, runtime.Inventory.ReservedBuyQuantity);
        runtime.Invalidate();
        QuoteDecision cancel = runtime.Evaluate();
        Assert.AreEqual(QuoteAction.Cancel, cancel.BidAction);
        Assert.IsTrue(runtime.TryAcceptIntent(true, cancel, out long intentId));
        Assert.IsTrue(intentId > 0);
        Assert.AreEqual(OrderStatus.PendingCancel, runtime.WorkingOrders.BidOrder.Status);
        Assert.AreEqual(QuoteAction.None, runtime.Evaluate().BidAction);
        runtime.OnExecution(true, 42, 3, 0, 3_000, OrderStatus.Cancelled);
        Assert.AreEqual(0L, runtime.Inventory.ReservedBuyQuantity);

        SignalMemory memory = runtime.SignalMemory;
        for (int i = 0; i < 8_193; i++) memory.AddTrade(0, 1, true, runtime.Config);
        Assert.IsTrue(memory.Faulted);
        Assert.IsFalse(runtime.BeginDepth(13, 13)); // update 12 is missing
        Assert.IsFalse(runtime.OrderBook.IsSynchronized);
        Assert.IsTrue(runtime.LoadSnapshot(
            [new BookLevel { PriceTicks = 100, Quantity = 30 }],
            [new BookLevel { PriceTicks = 102, Quantity = 10 }], 20));
        Assert.IsFalse(runtime.SignalMemory.Faulted);
        Assert.IsFalse(runtime.Ready); // Signals must warm again after recovery.
    }

    [TestMethod]
    public void AcceptedAndUnknownOrderOutcomesAreHandledConservatively()
    {
        var (runtime, ledger) = Runtime();
        Assert.IsTrue(runtime.BeginDepth(10, 10));
        runtime.EndDepth(0);
        Assert.IsTrue(runtime.BeginDepth(11, 11));
        runtime.EndDepth(1_000);
        QuoteDecision decision = runtime.Evaluate();
        Assert.AreEqual(QuoteAction.Place, decision.BidAction);
        decimal freeBefore = ledger.Free(1);
        Assert.IsTrue(runtime.TryAcceptIntent(true, decision, out long id));
        Assert.IsTrue(ledger.Free(1) < freeBefore);
        Assert.IsFalse(runtime.AcknowledgeOrderId(true, id, 0));
        runtime.RejectIntent(true, id);
        Assert.AreEqual(freeBefore, ledger.Free(1));
        Assert.AreEqual(QuoteAction.Place, runtime.Evaluate().BidAction);

        Assert.IsTrue(runtime.TryAcceptIntent(true, runtime.Evaluate(), out id));
        runtime.OnExecution(true, 123, 0, 0, 2_000, OrderStatus.New);
        Assert.IsFalse(runtime.OrderBook.IsSynchronized);

        var (acknowledged, _) = Runtime();
        Assert.IsTrue(acknowledged.BeginDepth(10, 10));
        acknowledged.EndDepth(0);
        Assert.IsTrue(acknowledged.BeginDepth(11, 11));
        acknowledged.EndDepth(1_000);
        Assert.IsTrue(acknowledged.TryAcceptIntent(true, acknowledged.Evaluate(), out id));
        Assert.IsTrue(acknowledged.AcknowledgeOrderId(true, id, 123));
        acknowledged.OnExecution(true, 123, 3, 100, 2_000, OrderStatus.PartiallyFilled);
        Assert.IsTrue(acknowledged.OrderBook.IsSynchronized);
        Assert.AreEqual(3L, acknowledged.WorkingOrders.BidOrder.FilledQuantity);
    }

    [TestMethod]
    public void AcceptedIntentsReserveSharedFundsUntilReleased()
    {
        var ledger = new AccountRiskLedger(["BTC", "USDT"]);
        ledger.BeginSnapshot(0);
        ledger.SetBalance(0, 0, 0);
        ledger.SetBalance(1, 100, 0);
        ledger.CompleteSnapshot();
        Assert.IsTrue(ledger.TryAccept(Instrument(), true, 60, 1, out long first));
        Assert.IsFalse(ledger.TryAccept(Instrument("ETHUSDT"), true, 60, 1, out _));
        Assert.AreEqual(40m, ledger.Free(1));
        Assert.IsTrue(ledger.Release(first));
        Assert.IsTrue(ledger.TryAccept(Instrument("ETHUSDT"), true, 60, 1, out _));

        ledger.BeginSnapshot(0);
        ledger.SetBalance(0, 0, 0);
        ledger.SetBalance(1, 40, 60);
        ledger.CompleteSnapshot();
        Assert.IsTrue(ledger.ReservePendingCancel(Instrument(), true, 60, 1, out long cancelHold));
        ledger.SetBalance(1, 100, 0); // Funds became free before cancellation confirmation.
        Assert.AreEqual(40m, ledger.Free(1));
        Assert.IsTrue(ledger.Release(cancelHold));
        Assert.AreEqual(100m, ledger.Free(1));
    }

    [TestMethod]
    public void SpscQueuePreservesValuesAcrossManyWraparounds()
    {
        var queue = new SpscRingBuffer<int>(8);
        for (int i = 0; i < 1_000; i++)
        {
            Assert.IsTrue(queue.TryEnqueue(i));
            Assert.IsTrue(queue.TryDequeue(out int value));
            Assert.AreEqual(i, value);
        }
        Assert.IsFalse(queue.TryDequeue(out _));
    }

    [TestMethod]
    public void WarmedBinaryDepthToDecisionAllocatesNoManagedBytes()
    {
        var (runtime, _) = Runtime();
        var feed = new BinanceFeedReader("", ["BTCUSDT"], "ws://127.0.0.1/stream",
            replay: true, logEvents: false, runtimes: [runtime]);
        int decisions = 0;
        feed.DecisionProduced += (_, _) => decisions++;
        byte[][] frames = Enumerable.Range(0, 400)
            .Select(i => Depth(10 + i, i * 10)).ToArray();
        for (int i = 0; i < 100; i++) feed.ProcessBinaryFrame(frames[i]);
        Assert.IsTrue(runtime.Ready);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 100; i < frames.Length; i++) feed.ProcessBinaryFrame(frames[i]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(400, decisions);
        Assert.AreEqual(0L, allocated);
    }

    [TestMethod]
    public void TradeFramesUpdateFlowAndDecisionWithoutAllocating()
    {
        var (runtime, _) = Runtime();
        var feed = new BinanceFeedReader("", ["BTCUSDT"], "ws://127.0.0.1/stream",
            replay: true, logEvents: false, runtimes: [runtime]);
        int decisions = 0;
        feed.DecisionProduced += (_, _) => decisions++;
        for (int i = 0; i < 101; i++) feed.ProcessBinaryFrame(Depth(10 + i, i * 10));
        byte[] frame = Trade(1_010);
        feed.ProcessBinaryFrame(frame);
        Assert.AreEqual(1.0, runtime.Signals.TradeFlowImbalance, 1e-12);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) feed.ProcessBinaryFrame(frame);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.AreEqual(202, decisions);
    }

    [TestMethod]
    public async Task FragmentedSocketReplayProducesIdenticalDecisionsOnTwoRuns()
    {
        var recording = new Recording(Guid.NewGuid(), Enumerable.Range(0, 4)
            .Select(i => new ReplayMessage(i + 1, i * 1_000_000L,
                WebSocketMessageType.Binary, Depth(10 + i, i * 10))));
        await using var server = ReplayServer.Create(recording, "http://127.0.0.1:0",
            reportCompleted: _ => { }, fragmentSize: 7);
        await server.StartAsync();
        string url = server.Urls.Single().Replace("http://", "ws://") + "/stream";
        var runs = new List<QuoteDecision[]>();
        for (int i = 0; i < 2; i++)
        {
            var (runtime, _) = Runtime();
            var reader = new BinanceFeedReader("", ["BTCUSDT"], url,
                replay: true, logEvents: false, runtimes: [runtime]);
            var decisions = new List<QuoteDecision>();
            reader.DecisionProduced += (_, decision) => decisions.Add(decision);
            await reader.Start(executionMode: ExecutionMode.Replay).WaitAsync(TimeSpan.FromSeconds(10));
            runs.Add(decisions.ToArray());
        }
        Assert.AreEqual(4, runs[0].Length);
        CollectionAssert.AreEqual(runs[0], runs[1]);
    }

    [TestMethod]
    public async Task CompleteAndFragmentedSocketPathHasZeroAllocationsAfterWarmup()
    {
        string directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "AssumptionZero.sln")))
            directory = Directory.GetParent(directory)?.FullName ?? throw new DirectoryNotFoundException();
        string app = Path.Combine(directory, "AssumptionZero.App/bin/Release/net10.0/AssumptionZero.App.dll");
        string fixture = Path.Combine(directory, "AssumptionZero.App/bin/Release/net10.0/replay-fixture.json");
        var recording = new Recording(Guid.NewGuid(), Enumerable.Range(0, 320)
            .Select(i => new ReplayMessage(i + 1, 0, WebSocketMessageType.Binary,
                Depth(40 + i, i * 60_000))));
        foreach (int fragmentSize in new[] { 0, 7 })
        {
            await using var server = ReplayServer.Create(recording, "http://127.0.0.1:0",
                reportCompleted: _ => { }, fragmentSize: fragmentSize);
            await server.StartAsync();
            string url = server.Urls.Single().Replace("http://", "ws://") + "/stream";
            using var process = new Process();
            process.StartInfo.FileName = DotnetExecutable();
            process.StartInfo.ArgumentList.Add(app);
            process.StartInfo.Environment["EXECUTION_MODE"] = "Replay";
            process.StartInfo.Environment["REPLAY_URL"] = url;
            process.StartInfo.Environment["REPLAY_FIXTURE"] = fixture;
            process.StartInfo.Environment["MEASURE_ALLOCATIONS"] = "1";
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;
            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.AreEqual(0, process.ExitCode, error + output);
            StringAssert.Contains(output, "MARKET_ALLOCATED_BYTES=0;READY_DECISIONS=");
        }
    }

    [TestMethod]
    public async Task StartupEd25519SignerProducesVerifiableSignature()
    {
        string directory = Path.Combine(Path.GetTempPath(), "az-sign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string key = Path.Combine(directory, "key.pem");
        string publicKey = Path.Combine(directory, "public.pem");
        string message = Path.Combine(directory, "message.txt");
        string signature = Path.Combine(directory, "signature.bin");
        try
        {
            Assert.AreEqual(0, await OpenSsl("genpkey", "-algorithm", "ed25519", "-out", key));
            Assert.AreEqual(0, await OpenSsl("pkey", "-in", key, "-pubout", "-out", publicKey));
            const string payload = "apiKey=TEST&timestamp=123456";
            await File.WriteAllTextAsync(message, payload);
            using var signer = new BinanceLiveBootstrap("TEST", key);
            byte[] bytes = Convert.FromBase64String(await signer.SignAsync(payload, CancellationToken.None));
            Assert.AreEqual(64, bytes.Length);
            await File.WriteAllBytesAsync(signature, bytes);
            Assert.AreEqual(0, await OpenSsl("pkeyutl", "-verify", "-pubin", "-inkey", publicKey,
                "-sigfile", signature, "-rawin", "-in", message));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task BuiltInDemoRunsAgainstBundledReplayFixture()
    {
        string directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "AssumptionZero.sln")))
            directory = Directory.GetParent(directory)?.FullName ?? throw new DirectoryNotFoundException();
        await using var server = ReplayServer.Create(DemoRecording.Create(), "http://127.0.0.1:0",
            reportCompleted: _ => { });
        await server.StartAsync();
        string url = server.Urls.Single().Replace("http://", "ws://") + "/stream";
        using var process = new Process();
        process.StartInfo.FileName = DotnetExecutable();
        process.StartInfo.ArgumentList.Add(Path.Combine(directory,
            "AssumptionZero.App/bin/Release/net10.0/AssumptionZero.App.dll"));
        process.StartInfo.Environment["EXECUTION_MODE"] = "Replay";
        process.StartInfo.Environment["REPLAY_URL"] = url;
        process.StartInfo.Environment["REPLAY_FIXTURE"] = Path.Combine(directory,
            "AssumptionZero.App/bin/Release/net10.0/replay-fixture.json");
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.Start();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        Assert.AreEqual(0, process.ExitCode, error + output);
        StringAssert.Contains(output, "Replay complete: 120 decisions, ");
        Assert.IsFalse(output.Contains("120 decisions, 0 with", StringComparison.Ordinal));
    }

    private static async Task<int> OpenSsl(params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo.FileName = "openssl";
        foreach (string argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.UseShellExecute = false;
        process.Start();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static string DotnetExecutable()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "dotnet");
        return File.Exists(local) ? local : "dotnet";
    }
}
