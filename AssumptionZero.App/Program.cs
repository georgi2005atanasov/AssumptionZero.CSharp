using AssumptionZero.App;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.InMemory;
using AssumptionZero.Infrastructure;
using AssumptionZero.Infrastructure.External;
using AssumptionZero.Infrastructure.Benchmarking;

string binanceApiKey = Environment.GetEnvironmentVariable("BINANCE_APIKEY") ??
                       "my-test-env-key";
string connectionString = Environment.GetEnvironmentVariable("PG_CONNECTION") ??
                          "Host=172.17.0.2;Database=assumption_zero;Username=postgres;Password=mysecretpassword;Trust Server Certificate=true;";

string executionMode = Environment.GetEnvironmentVariable("EXECUTION_MODE") ?? "Replay";
if (!Enum.TryParse(executionMode, ignoreCase: true, out ExecutionMode mode) || !Enum.IsDefined(mode))
    throw new ArgumentException("EXECUTION_MODE must be SeedData, NormalExecution, or Replay.");
bool replay = mode == ExecutionMode.Replay;
string? logSetting = Environment.GetEnvironmentVariable("LOG_EVENTS");
bool logEvents = logSetting is not null && bool.Parse(logSetting);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};

string[] symbols =
[
    "BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "XRPUSDT",
    "ADAUSDT", "DOGEUSDT", "AVAXUSDT", "LINKUSDT", "DOTUSDT",
    "LTCUSDT", "BCHUSDT", "TRXUSDT", "XLMUSDT", "UNIUSDT",
    "ATOMUSDT", "NEARUSDT", "ETCUSDT", "APTUSDT", "FILUSDT",
    "SUIUSDT", "ARBUSDT", "OPUSDT", "INJUSDT", "ICPUSDT",
    "SHIBUSDT", "PEPEUSDT", "WIFUSDT", "RENDERUSDT", "POLUSDT",
    "AAVEUSDT", "FETUSDT", "RUNEUSDT", "SEIUSDT", "TIAUSDT",
    "ALGOUSDT", "HBARUSDT", "VETUSDT", "JUPUSDT", "0GUSDT",
    "XTZUSDT", "IOTAUSDT", "THETAUSDT", "EGLDUSDT", "KAVAUSDT",
    "ZECUSDT", "DASHUSDT", "NEOUSDT", "QTUMUSDT", "ZILUSDT",
    "ONEUSDT", "ROSEUSDT", "MINAUSDT", "KSMUSDT", "LRCUSDT",
    "SKLUSDT", "STXUSDT", "RSRUSDT", "SSVUSDT", "ARUSDT",
    "1INCHUSDT", "DYDXUSDT", "TWTUSDT", "CAKEUSDT", "SUPERUSDT",
    "WOOUSDT", "PEOPLEUSDT", "GRTUSDT", "IMXUSDT", "SANDUSDT",
    "MANAUSDT", "AXSUSDT", "GALAUSDT", "CHZUSDT", "APEUSDT",
    "FLOWUSDT", "LDOUSDT", "CRVUSDT", "COMPUSDT", "SUSHIUSDT",
    "ENAUSDT", "WLDUSDT", "ONDOUSDT", "PENDLEUSDT", "PYTHUSDT",
    "JTOUSDT", "STRKUSDT", "ZROUSDT", "ZKUSDT", "AEVOUSDT",
    "ETHFIUSDT", "EIGENUSDT", "TAOUSDT", "BOMEUSDT", "FLOKIUSDT",
    "BONKUSDT", "TRUMPUSDT", "PAXGUSDT", "ANKRUSDT", "JASMYUSDT"
];
try
{
    switch (mode)
    {
        case ExecutionMode.SeedData:
            var recorder = new BinanceFeedReader(binanceApiKey, symbols,
                "wss://stream-sbe.testnet.binance.vision/stream?streams=", logEvents: logEvents);
            await recorder.Start(cancellation.Token, mode, connectionString);
            break;
        case ExecutionMode.NormalExecution:
        {
            if (Environment.GetEnvironmentVariable("BINANCE_APIKEY") is null)
                throw new InvalidOperationException("BINANCE_APIKEY is required in NormalExecution.");
            string keyPath = Environment.GetEnvironmentVariable("BINANCE_ED25519_PRIVATE_KEY_PATH") ??
                throw new InvalidOperationException("BINANCE_ED25519_PRIVATE_KEY_PATH is required in NormalExecution.");
            using var bootstrap = new BinanceLiveBootstrap(binanceApiKey, keyPath);
            var instruments = await bootstrap.LoadInstrumentsAsync(symbols, cancellation.Token);
            string[] assets = instruments.SelectMany(i => new[] { i.BaseAsset, i.QuoteAsset })
                .Distinct(StringComparer.Ordinal).ToArray();
            var ledger = new AccountRiskLedger(assets);
            HotConfig config = new();
            InstrumentRuntime[] runtimes = instruments.Select(i =>
                new InstrumentRuntime(i, config, ledger, 0, config.BaseQuantity * 10)).ToArray();
            await using var accountStream = new BinanceAccountStream(binanceApiKey, bootstrap, ledger, runtimes);
            await accountStream.StartAsync(cancellation.Token);
            bool first = true;
            var feed = new BinanceFeedReader(binanceApiKey, instruments.Select(i => i.Symbol).ToArray(),
                "wss://stream-sbe.testnet.binance.vision/stream?streams=", logEvents: logEvents,
                runtimes: runtimes, accountStream: accountStream);
            while (!cancellation.IsCancellationRequested)
            {
                try
                {
                    ledger.Invalidate();
                    await bootstrap.LoadAccountAsync(ledger, runtimes, cancellation.Token, initializeInventory: first);
                    first = false;
                    await feed.Start(cancellation.Token, mode,
                        afterConnect: ct => bootstrap.LoadBooksAsync(runtimes, ct));
                    break;
                }
                catch (IOException exception) when (!cancellation.IsCancellationRequested && !accountStream.Faulted)
                {
                    ledger.Invalidate();
                    foreach (InstrumentRuntime runtime in runtimes) runtime.Invalidate();
                    Console.Error.WriteLine($"Market feed resynchronizing: {exception.Message}");
                    await Task.Delay(1_000, cancellation.Token);
                }
            }
            break;
        }
        case ExecutionMode.Replay:
        {
            HotConfig config = new();
            string? fixturePath = Environment.GetEnvironmentVariable("REPLAY_FIXTURE");
            bool syntheticReplay = string.IsNullOrWhiteSpace(fixturePath);
            var fixture = syntheticReplay ? BenchmarkReplayFixture.Create(symbols, config) :
                ReplayFixture.Load(fixturePath!, config);
            if (syntheticReplay)
                Console.WriteLine("Performance replay: synthetic account, filters, and initial books; quote values are not historical results.");
            var feed = new BinanceFeedReader("", fixture.Symbols,
                Environment.GetEnvironmentVariable("REPLAY_URL") ?? "ws://127.0.0.1:5080/stream",
                replay: true, logEvents: logEvents, runtimes: fixture.Runtimes,
                syntheticReplay: syntheticReplay);
            string? benchmarkOutput = Environment.GetEnvironmentVariable("BENCHMARK_OUTPUT");
            using ReplayBenchmarkRecorder? benchmark = benchmarkOutput is null ? null : new(
                int.Parse(Environment.GetEnvironmentVariable("BENCHMARK_EXPECTED_FRAMES")
                    ?? throw new InvalidOperationException("BENCHMARK_EXPECTED_FRAMES is required.")),
                int.Parse(Environment.GetEnvironmentVariable("BENCHMARK_WARMUP_FRAMES") ?? "0"),
                Environment.GetEnvironmentVariable("BENCHMARK_DIAGNOSTIC") == "1");
            feed.Benchmark = benchmark;
            int decisions = 0, actionable = 0;
            feed.DecisionProduced += (_, decision) =>
            {
                decisions++;
                if (decision.BidAction is QuoteAction.Place or QuoteAction.Replace or QuoteAction.Cancel ||
                    decision.AskAction is QuoteAction.Place or QuoteAction.Replace or QuoteAction.Cancel)
                    actionable++;
            };
            bool measure = Environment.GetEnvironmentVariable("MEASURE_ALLOCATIONS") == "1";
            int readyDecisions = 0;
            long allocationStart = 0, allocated = -1;
            if (measure)
                feed.DecisionProduced += (runtime, _) =>
                {
                    if (!runtime.Ready) return;
                    readyDecisions++;
                    if (readyDecisions == 100) allocationStart = GC.GetTotalAllocatedBytes(false);
                    if (readyDecisions == 200)
                        allocated = GC.GetTotalAllocatedBytes(false) - allocationStart;
                };
            bool completed = false;
            try
            {
                await feed.Start(cancellation.Token, mode);
                completed = true;
            }
            finally
            {
                if (benchmark is not null) benchmark.Finish(completed).Write(benchmarkOutput!);
            }
            Console.WriteLine($"Replay complete: {decisions} decisions, {actionable} with place/replace/cancel intents.");
            Console.WriteLine($"Products: {fixture.Runtimes.Count(runtime => runtime.OrderBook.IsSynchronized)} " +
                              $"with a book, {fixture.Runtimes.Count(runtime => runtime.Ready)} ready " +
                              $"of {fixture.Runtimes.Length} configured.");
            if (measure) Console.WriteLine($"MARKET_ALLOCATED_BYTES={allocated};READY_DECISIONS={readyDecisions}");
            break;
        }
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("Stopped.");
}
