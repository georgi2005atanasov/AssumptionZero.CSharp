using AssumptionZero.Application.Interfaces.Services;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.Entities;
using AssumptionZero.Domain.InMemory;
using AssumptionZero.Infrastructure;
using AssumptionZero.Infrastructure.Benchmarking;
using AssumptionZero.Infrastructure.External;

namespace AssumptionZero.App;

public class ExecutionRouter
{
    public async Task SeedData(string binanceApiKey,
        ExecutionMode mode,
        string connectionString,
        string[] symbols,
        bool logEvents,
        CancellationTokenSource cancellation
    )
    {
        var recorder = CreateBinanceFeedReader(
            binanceApiKey,
            AppConstants.BaseSbeStreamUrl,
            symbols,
            logEvents
        );
        await recorder.Start(cancellation.Token, mode, connectionString);
    }

    public async Task NormalExecution(ExecutionMode mode,
        string[] symbols,
        bool logEvents,
        CancellationTokenSource cancellation
    )
    {
        // check for the needed config
        string binanceApiKey = ThrowIfMissingOrReturnValue(EnvKeys.BINANCE_API_KEY);
        string keyPath = ThrowIfMissingOrReturnValue(EnvKeys.BINANCE_ED25519_PRIVATE_KEY_PATH);

        // prepare instruments' runtimes
        using var bootstrap = new BinanceLiveBootstrap(binanceApiKey, keyPath);
        var instruments = await bootstrap.LoadInstruments(symbols, cancellation.Token);
        var ledger = GetAccountRiskLedger(in instruments);
        InstrumentRuntime[] runtimes = GetRuntimes(in instruments, ledger);

        await using var accountStream = new BinanceAccountStream(binanceApiKey, bootstrap, ledger, runtimes);
        await accountStream.Start(cancellation.Token);

        bool first = true;
        var binanceFeedReader = CreateBinanceFeedReader(
            binanceApiKey,
            AppConstants.BaseSbeStreamUrl,
            instruments.Select(i => i.Symbol).ToArray(),
            logEvents: logEvents,
            runtimes: runtimes,
            accountStream: accountStream
        );

        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                ledger.Invalidate();
                await bootstrap.LoadAccount(ledger, runtimes, cancellation.Token, initializeInventory: first);
                first = false;
                await binanceFeedReader.Start(
                    cancellation.Token,
                    mode,
                    afterConnect: ct => bootstrap.LoadBooks(runtimes, ct)
                );
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
    }

    public async Task Replay(ExecutionMode mode,
        string[] symbols,
        bool logEvents,
        CancellationTokenSource cancellation
    )
    {
        HotConfig config = new();
        string? fixturePath = EnvironmentVariables.Get(EnvKeys.REPLAY_FIXTURE);
        bool syntheticReplay = string.IsNullOrWhiteSpace(fixturePath);
        var fixture = syntheticReplay
            ? BenchmarkReplayFixture.Create(symbols, config)
            : ReplayFixture.Load(fixturePath!, config);
        if (syntheticReplay)
            Console.WriteLine(
                "Performance replay: synthetic account, filters, and initial books; quote values are not historical results.");
        var feed = new BinanceFeedReader(
            string.Empty,
            fixture.Symbols,
            EnvironmentVariables.Get(EnvKeys.REPLAY_URL) ??
            AppConstants.DefaultReplayUrl,
            replay: true,
            logEvents: logEvents,
            runtimes: fixture.Runtimes,
            syntheticReplay: syntheticReplay
        );

        string? benchmarkOutput = EnvironmentVariables.Get(EnvKeys.BENCHMARK_OUTPUT);
        using ReplayBenchmarkRecorder? benchmark = benchmarkOutput is null
            ? null
            : new(
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
        bool measure = EnvironmentVariables.Get(EnvKeys.MEASURE_ALLOCATIONS) == "1";
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

        Console.WriteLine(
            $"Replay complete: {decisions} decisions, {actionable} with place/replace/cancel intents.");
        Console.WriteLine($"Products: {fixture.Runtimes.Count(runtime => runtime.OrderBook.IsSynchronized)} " +
                          $"with a book, {fixture.Runtimes.Count(runtime => runtime.Ready)} ready " +
                          $"of {fixture.Runtimes.Length} configured.");
        if (measure) Console.WriteLine($"MARKET_ALLOCATED_BYTES={allocated};READY_DECISIONS={readyDecisions}");
    }

    private static BinanceFeedReader CreateBinanceFeedReader(
        string binanceApiKey,
        string baseUrl,
        string[] symbols,
        bool logEvents,
        InstrumentRuntime[]? runtimes = null,
        BinanceAccountStream? accountStream = null,
        bool replay = false)
        => new(
            binanceApiKey,
            symbols,
            baseUrl,
            replay,
            logEvents,
            runtimes,
            accountStream
        );

    private static string ThrowIfMissingOrReturnValue(EnvKeys key)
    {
        if (EnvironmentVariables.Get(key) is null)
            throw new InvalidOperationException($"{key} not found.");

        return key.ToString();
    }

    private static InstrumentRuntime[] GetRuntimes(in Instrument[] instruments,
        AccountRiskLedger ledger)
    {
        HotConfig config = new();
        InstrumentRuntime[] runtimes = instruments
            .Select(i =>
                new InstrumentRuntime(
                    i,
                    config,
                    ledger,
                    0,
                    config.BaseQuantity * 10)
            )
            .ToArray();
        return runtimes;
    }

    private static AccountRiskLedger GetAccountRiskLedger(in Instrument[] instruments)
    {
        string[] assets = instruments
            .SelectMany(i => new[] { i.BaseAsset, i.QuoteAsset })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var ledger = new AccountRiskLedger(assets);
        return ledger;
    }
}