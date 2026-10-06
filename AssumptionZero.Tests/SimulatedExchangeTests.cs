using AssumptionZero.Infrastructure;
using AssumptionZero.Infrastructure.Simulation;
using AssumptionZero.Simulator;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static AssumptionZero.Infrastructure.Simulation.ExchangeProtocol;

namespace AssumptionZero.Tests;

[TestClass]
public class SimulatedExchangeTests
{
    private static SimManifest Manifest(string scenario = "mixed") => new(1, "test", "deterministic", 100, 10, "0.001",
        [new(0, "BTCUSDT", "1", "1")], ["USDT", "BTC"], ["10000", "100"], scenario);
    private static OrderCommand Place(long id, bool buy = true, long qty = 10) => new(id, id, 0, buy, "place", 0, 100, qty, 0);
    private static decimal Free(SimulatedExchange x, int i) => Money(x.Snapshot()[i].Free);
    private static decimal Locked(SimulatedExchange x, int i) => Money(x.Snapshot()[i].Locked);

    [TestMethod]
    public void BuyAndSellFeesConserveAssetsAndReleaseAllHolds()
    {
        var x = new SimulatedExchange(Manifest());
        var buy = x.Commands([Place(1)], 0).Single();
        Assert.AreEqual("ack", buy.Kind); Assert.AreEqual(8999m, Free(x, 0)); Assert.AreEqual(1001m, Locked(x, 0));
        Assert.AreEqual(0, x.Advance(3).Count); Assert.AreEqual(4, x.Advance(4).Single().Status);
        Assert.AreEqual(110m, Free(x, 1)); Assert.AreEqual(0m, Locked(x, 0));
        x.Commands([Place(2, false)], 4);
        var partial = x.Advance(6).Single(); Assert.AreEqual(3, partial.Status); Assert.AreEqual(5L, partial.Filled);
        Assert.AreEqual(9498.5m, Free(x, 0)); Assert.AreEqual(5m, Locked(x, 1));
        x.Advance(8); Assert.AreEqual(9998m, Free(x, 0)); Assert.AreEqual(100m, Free(x, 1));
        Assert.AreEqual(2m, x.Fees); Assert.AreEqual(0, x.OpenOrders);
    }

    [TestMethod]
    public void CancelAfterPartialFillReleasesOnlyRemainingHold()
    {
        var x = new SimulatedExchange(Manifest()); x.Commands([Place(1)], 0); x.Advance(4);
        long order = x.Commands([Place(2)], 4).Single().OrderId; x.Advance(6);
        var cancelled = x.Commands([new(3, 3, 0, true, "cancel", order, 100, 10, 0)], 6).Single();
        Assert.AreEqual(5, cancelled.Status); Assert.AreEqual(5L, cancelled.Filled);
        Assert.AreEqual(8498.5m, Free(x, 0)); Assert.AreEqual(115m, Free(x, 1)); Assert.AreEqual(0m, Locked(x, 0));
        Assert.AreEqual(0, x.Advance(8).Count);
    }

    [TestMethod]
    public void FillWinsCancelRaceAndDuplicateCommandFailsClosed()
    {
        var x = new SimulatedExchange(Manifest()); long order = x.Commands([Place(1)], 0).Single().OrderId;
        x.Advance(4); var cancel = new OrderCommand(2, 2, 0, true, "cancel", order, 100, 10, 0);
        Assert.AreEqual("unknown_or_terminal_order", x.Commands([cancel], 4).Single().Reason);
        Assert.AreEqual(8999m, Free(x, 0)); Assert.AreEqual(110m, Free(x, 1));
        Assert.ThrowsException<InvalidDataException>(() => x.Commands([cancel], 4));
    }

    [TestMethod]
    public void RejectedAndRestingOrdersLeaveNoFundsBehind()
    {
        var x = new SimulatedExchange(Manifest("rest"));
        Assert.AreEqual("reject", x.Commands([Place(1, qty: 1000)], 0).Single().Kind);
        Assert.AreEqual(10000m, Free(x, 0)); x.Commands([Place(2)], 0);
        Assert.AreEqual(0, x.Advance(100).Count); Assert.AreEqual(5, x.Finish().Single().Status);
        Assert.AreEqual(10000m, Free(x, 0)); Assert.AreEqual(0m, Locked(x, 0)); Assert.AreEqual(0, x.OpenOrders);
    }

    [TestMethod]
    public async Task ReferenceSpscQueuePreservesOrderAcrossWraparound()
    {
        var q = new SpscRingBuffer<string>(16); const int n = 100000;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var producer = Task.Run(() => { for (int i = 0; i < n; i++) { string value = i.ToString(); while (!q.TryEnqueue(value)) { timeout.Token.ThrowIfCancellationRequested(); Thread.Yield(); } } });
        var consumer = Task.Run(() => { for (int i = 0; i < n; i++) { string value; while (!q.TryDequeue(out value)) { timeout.Token.ThrowIfCancellationRequested(); Thread.Yield(); } Assert.AreEqual(i.ToString(), value); } });
        await Task.WhenAll(producer, consumer); Assert.AreEqual(0, q.Count); Assert.IsFalse(q.TryDequeue(out _));
        for (int i = 0; i < 16; i++) Assert.IsTrue(q.TryEnqueue("x")); Assert.IsFalse(q.TryEnqueue("overflow"));
    }
    [TestMethod]
    public void ClientAcceptsFillThenLateCancelRejectWithoutReopeningOrder()
    {
        var ledger = new AssumptionZero.Application.Services.AccountRiskLedger(["USDT", "BTC"]);
        ledger.BeginSnapshot(.001m); ledger.SetBalance(0, 8999, 1001); ledger.SetBalance(1, 100, 0); ledger.CompleteSnapshot();
        var instrument = new AssumptionZero.Domain.Entities.Instrument(0, "BTCUSDT", "BTC", "USDT", 1, 1, 1, 100000, 0);
        var runtime = new AssumptionZero.Application.Services.InstrumentRuntime(instrument, new(), ledger, 100, 200);
        runtime.WorkingOrders = new() { BidOrder = new() { OrderId = 42, PriceTicks = 100, Quantity = 10, Status = AssumptionZero.Domain.InMemory.OrderStatus.New, IsActive = true } };
        var trader = new SimulationTrader(ledger, [runtime]); trader.Decide(0);
        var cancel = trader.Outbox.Single(); Assert.AreEqual("cancel", cancel.Action);
        trader.Apply(new(1, 99, 99, 0, true, 42, "execution", 4, 10, 100, "", [new(0, "8999", "0"), new(1, "110", "0")], 0));
        var reject = new ExecutionEvent(2, cancel.Id, cancel.Intent, 0, true, 42, "reject", 6, 0, 100, "unknown_or_terminal_order", [], 0);
        trader.Apply(reject); trader.Apply(reject); trader.AssertSettled();
        Assert.AreEqual(110L, runtime.Inventory.CurrentQuantity); Assert.AreEqual(0m, ledger.Reserved(0));
        Assert.ThrowsException<InvalidDataException>(() => trader.Apply(reject with { Price = 101 }));
    }

    [TestMethod]
    public void RiskReservationIncludesBuyFee()
    {
        var ledger = new AssumptionZero.Application.Services.AccountRiskLedger(["USDT", "BTC"]);
        ledger.BeginSnapshot(.001m); ledger.SetBalance(0, 1000, 0); ledger.SetBalance(1, 100, 0); ledger.CompleteSnapshot();
        var instrument = new AssumptionZero.Domain.Entities.Instrument(0, "BTCUSDT", "BTC", "USDT", 1, 1, 1, 100000, 0);
        Assert.IsFalse(ledger.TryAccept(instrument, true, 100, 10, out _));
        ledger.SetBalance(0, 1001, 0); Assert.IsTrue(ledger.TryAccept(instrument, true, 100, 10, out long id));
        Assert.AreEqual(1001m, ledger.Reserved(0)); Assert.AreEqual(0m, ledger.Free(0));
        Assert.IsTrue(ledger.Release(id)); Assert.AreEqual(1001m, ledger.Free(0));
    }

    [TestMethod]
    public async Task BothClientsRepeatDeterministicTraceAndReconcileAsyncSessions()
    {
        string? rust = Environment.GetEnvironmentVariable("RUST_APP_TEST");
        if (rust is null || !File.Exists(rust)) Assert.Inconclusive("Set RUST_APP_TEST to the Release Rust client.");
        string output = Path.Combine(Path.GetTempPath(), "az-closed-loop-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            int code = await ClosedLoopRunner.Run(["--recording", "demo", "--max-messages", "120", "--target-duration-seconds", "1", "--repetitions", "1", "--rust-app", rust!, "--out", output], CancellationToken.None);
            Assert.AreEqual(0, code);
            using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "closed-loop-comparison.json")));
            Assert.IsTrue(report.RootElement.GetProperty("exact_deterministic_equivalence").GetBoolean());
            Assert.AreEqual(8, report.RootElement.GetProperty("runs").GetArrayLength());
            foreach (var language in new[] { "CSharp", "Rust" })
            {
                using var measured = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, $"run-1-{language}.json")));
                Assert.AreEqual(108, measured.RootElement.GetProperty("processing_ns").GetProperty("count").GetInt32());
                Assert.IsTrue(File.Exists(Path.Combine(output, $"run-1-{language}.json.samples.csv")));
            }
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }

    [TestMethod]
    public async Task ClientStartupFailureStopsCoordinatorPromptly()
    {
        string output = Path.Combine(Path.GetTempPath(), "az-closed-loop-fail-" + Guid.NewGuid().ToString("N"));
        try
        {
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => await ClosedLoopRunner.Run(
                ["--recording", "demo", "--max-messages", "120", "--language", "Rust", "--rust-app", "/bin/false", "--out", output], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally { if (Directory.Exists(output)) Directory.Delete(output, true); }
    }

    [DataTestMethod]
    [DataRow("Rust")]
    [DataRow("CSharp")]
    public async Task PacedClientsSurviveFrequentWebSocketKeepAlives(string language)
    {
        string? rust = Environment.GetEnvironmentVariable("RUST_APP_TEST");
        if (language == "Rust" && (rust is null || !File.Exists(rust))) Assert.Inconclusive("Set RUST_APP_TEST to the Release Rust client.");
        var recording = DemoRecording.Create();
        var manifest = ClosedLoopSession.Manifest(recording, ["BTCUSDT"], "async", "mixed");
        var session = new ClosedLoopSession(recording, manifest, 2);
        await using var server = session.Create(TimeSpan.FromMilliseconds(25));
        await server.StartAsync();
        string file = Path.Combine(Path.GetTempPath(), "az-keepalive-" + Guid.NewGuid().ToString("N") + ".json");
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        var start = new System.Diagnostics.ProcessStartInfo(language == "Rust" ? rust! : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/home/sparrow/.dotnet/dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        if (language == "CSharp") start.ArgumentList.Add(Path.Combine(root, "AssumptionZero.App/bin/Release/net10.0/AssumptionZero.App.dll"));
        start.Environment["EXECUTION_MODE"] = "SimulatedExchange";
        start.Environment["SIM_EXCHANGE_URL"] = server.Urls.Single().Replace("http://", "ws://");
        start.Environment["SIM_OUTPUT"] = file; start.Environment["SIM_MEASURE"] = "true";
        using var child = System.Diagnostics.Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(); var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serving = session.Run(timeout.Token);
        try
        {
            await child.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, child.ExitCode, await stderr);
            await serving;
            using var result = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(file));
            Assert.IsTrue(result.RootElement.GetProperty("completed").GetBoolean());
            Assert.AreEqual(120, result.RootElement.GetProperty("frames").GetInt32());
            Assert.AreEqual(session.Exchange.Sequence, result.RootElement.GetProperty("reports").GetInt64());
            Assert.AreEqual(0, session.Exchange.OpenOrders);
        }
        finally
        {
            await timeout.CancelAsync(); if (!child.HasExited) child.Kill(true);
            await child.WaitForExitAsync(); await stdout; await stderr;
            try { await serving; } catch { }
            await server.StopAsync();
            File.Delete(file); File.Delete(file + ".samples.csv");
        }
    }

}
