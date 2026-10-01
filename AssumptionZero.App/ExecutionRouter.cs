using AssumptionZero.Application.Services;
using AssumptionZero.Domain.InMemory;
using AssumptionZero.Infrastructure.Benchmarking;
using AssumptionZero.Infrastructure.External;

namespace AssumptionZero.App;

public sealed class ExecutionRouter
{
    public async Task SeedData(string apiKey, string connectionString, string[] symbols,
        bool logEvents, CancellationToken cancellationToken)
    {
        var feed = new BinanceFeedReader(apiKey, symbols, AppConstants.BaseSbeStreamUrl,
            FeedMode.Capture, logEvents);
        await feed.Start(cancellationToken, connectionString);
    }

    public async Task NormalExecution(string[] symbols, bool logEvents, CancellationToken cancellationToken)
    {
        string apiKey = Required(EnvKeys.BINANCE_API_KEY);
        string privateKeyPath = Required(EnvKeys.BINANCE_ED25519_PRIVATE_KEY_PATH);
        using var bootstrap = new BinanceLiveBootstrap(apiKey, privateKeyPath);
        var instruments = await bootstrap.LoadInstruments(symbols, cancellationToken);
        string[] assets = instruments.SelectMany(i => new[] { i.BaseAsset, i.QuoteAsset })
            .Distinct(StringComparer.Ordinal).ToArray();
        var ledger = new AccountRiskLedger(assets);
        HotConfig config = new();
        InstrumentRuntime[] runtimes = instruments.Select(i =>
            new InstrumentRuntime(i, config, ledger, 0, config.BaseQuantity * 10)).ToArray();

        await using var accountStream = new BinanceAccountStream(apiKey, bootstrap, ledger, runtimes);
        await accountStream.Start(cancellationToken);
        var feed = new BinanceFeedReader(apiKey, instruments.Select(i => i.Symbol).ToArray(),
            AppConstants.BaseSbeStreamUrl, FeedMode.Live, logEvents, runtimes, accountStream);

        bool first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ledger.Invalidate();
                await bootstrap.LoadAccount(ledger, runtimes, cancellationToken, initializeInventory: first);
                first = false;
                await feed.Start(cancellationToken, afterConnect: ct => bootstrap.LoadBooks(runtimes, ct));
                break;
            }
            catch (IOException exception) when (!cancellationToken.IsCancellationRequested && !accountStream.Faulted)
            {
                ledger.Invalidate();
                foreach (InstrumentRuntime runtime in runtimes) runtime.Invalidate();
                Console.Error.WriteLine($"Market feed resynchronizing: {exception.Message}");
                await Task.Delay(1_000, cancellationToken);
            }
        }
    }

    public async Task Replay(string[] symbols, bool logEvents, CancellationToken cancellationToken)
    {
        string? fixturePath = EnvironmentVariables.Get(EnvKeys.REPLAY_FIXTURE);
        bool estimateBook = string.IsNullOrWhiteSpace(fixturePath);
        HotConfig config = new();
        var state = estimateBook
            ? EstimatedReplayState.Create(symbols, config)
            : FixtureReplayState.Load(fixturePath!, config);
        if (estimateBook)
            Console.WriteLine("Performance replay: account and starting books are estimated; quote values are not historical results.");

        var feed = new BinanceFeedReader(string.Empty, state.Symbols,
            EnvironmentVariables.Get(EnvKeys.REPLAY_URL) ?? AppConstants.DefaultReplayUrl,
            estimateBook ? FeedMode.ReplayWithEstimatedBook : FeedMode.ReplayWithSnapshot,
            logEvents, state.Runtimes);

        string? benchmarkOutput = EnvironmentVariables.Get(EnvKeys.BENCHMARK_OUTPUT);
        using ReplayBenchmarkRecorder? benchmark = benchmarkOutput is null ? null : new(
            int.Parse(Environment.GetEnvironmentVariable("BENCHMARK_EXPECTED_FRAMES")
                      ?? throw new InvalidOperationException("BENCHMARK_EXPECTED_FRAMES is required.")),
            int.Parse(Environment.GetEnvironmentVariable("BENCHMARK_WARMUP_FRAMES") ?? "0"),
            Environment.GetEnvironmentVariable("BENCHMARK_DIAGNOSTIC") == "1");
        feed.Benchmark = benchmark;

        string? tracePath = Environment.GetEnvironmentVariable("CORRECTNESS_TRACE");
        using StreamWriter? trace = tracePath is null ? null : new StreamWriter(tracePath);
        int decisions = 0, actionable = 0, readyDecisions = 0;
        long allocationStart = 0, allocated = -1;
        bool measureAllocations = EnvironmentVariables.Get(EnvKeys.MEASURE_ALLOCATIONS) == "1";
        feed.DecisionProduced += (runtime, decision) =>
        {
            decisions++;
            if (trace is not null) trace.WriteLine(System.Text.Json.JsonSerializer.Serialize(new long[] {
                runtime.Instrument.Id, decision.BidPriceTicks, decision.BidQuantity, (long)decision.BidAction,
                decision.AskPriceTicks, decision.AskQuantity, (long)decision.AskAction }));
            if (decision.BidAction is QuoteAction.Place or QuoteAction.Replace or QuoteAction.Cancel ||
                decision.AskAction is QuoteAction.Place or QuoteAction.Replace or QuoteAction.Cancel)
                actionable++;
            if (!measureAllocations || !runtime.Ready) return;
            readyDecisions++;
            if (readyDecisions == 100) allocationStart = GC.GetTotalAllocatedBytes(false);
            if (readyDecisions == 200) allocated = GC.GetTotalAllocatedBytes(false) - allocationStart;
        };

        bool completed = false;
        try
        {
            await feed.Start(cancellationToken);
            completed = true;
        }
        finally
        {
            if (benchmark is not null) (benchmark.Finish(completed) with {
                FinalStateHash = DiscreteStateHash.Compute(state.Runtimes) }).Write(benchmarkOutput!);
        }

        Console.WriteLine($"Replay complete: {decisions} decisions, {actionable} with place/replace/cancel intents.");
        Console.WriteLine($"Products: {state.Runtimes.Count(runtime => runtime.OrderBook.IsSynchronized)} " +
                          $"with a book, {state.Runtimes.Count(runtime => runtime.Ready)} ready " +
                          $"of {state.Runtimes.Length} configured.");
        if (measureAllocations)
            Console.WriteLine($"MARKET_ALLOCATED_BYTES={allocated};READY_DECISIONS={readyDecisions}");
    }

    private static string Required(EnvKeys key) =>
        EnvironmentVariables.Get(key) is { Length: > 0 } value ? value :
            throw new InvalidOperationException($"{key} not found.");
}
