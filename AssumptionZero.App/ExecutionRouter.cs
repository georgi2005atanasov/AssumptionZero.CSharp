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
        var (binanceApiKey, keyPath) = GetNormalExecutionEnvVars();

        // prepare instruments' runtimes
        using var bootstrap = new BinanceLiveBootstrap(binanceApiKey, keyPath);
        var instruments = await bootstrap.LoadInstruments(symbols, cancellation.Token);
        var ledger = GetAccountRiskLedger(in instruments);
        InstrumentRuntime[] runtimes = GetRuntimes(in instruments, ledger);

        // start the account stream
        await using var accountStream = new BinanceAccountStream(binanceApiKey, bootstrap, ledger, runtimes);
        await accountStream.Start(cancellation.Token);

        var binanceFeedReader = CreateBinanceFeedReader(
            binanceApiKey,
            AppConstants.BaseSbeStreamUrl,
            instruments.Select(i => i.Symbol).ToArray(),
            logEvents: logEvents,
            runtimes: runtimes,
            accountStream: accountStream
        );

        await RunNormalExecutionLoop(
            mode,
            binanceFeedReader,
            bootstrap,
            instruments,
            runtimes,
            ledger,
            accountStream,
            cancellation
        );
    }

    public async Task Replay(ExecutionMode mode,
        string[] symbols,
        bool logEvents,
        CancellationTokenSource cancellation
    )
    {
        var (usedSymbols, runtimes, syntheticReplay) = GetFixture(symbols);
        var feed = CreateBinanceFeedReader(
            string.Empty,
            EnvironmentVariables.Get(EnvKeys.REPLAY_URL) ?? AppConstants.DefaultReplayUrl,
            usedSymbols,
            replay: true,
            logEvents: logEvents,
            runtimes: runtimes,
            syntheticReplay: syntheticReplay
        );
        var (benchmark,
            decisions,
            readyDecisions,
            actionable,
            allocated,
            measure,
            benchmarkOutput) = InitializeBenchmarks(feed);

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

        LogOutcome(decisions,
            readyDecisions,
            actionable,
            allocated,
            measure,
            runtimes
        );
    }

    private static BinanceFeedReader CreateBinanceFeedReader(
        string binanceApiKey,
        string baseUrl,
        string[] symbols,
        bool logEvents,
        InstrumentRuntime[]? runtimes = null,
        BinanceAccountStream? accountStream = null,
        bool replay = false,
        bool syntheticReplay = false)
        => new(
            binanceApiKey,
            symbols,
            baseUrl,
            replay,
            logEvents,
            runtimes,
            accountStream,
            syntheticReplay
        );

    #region NormalExecution

    // maybe another class could be created but I am gonna be lazy and just use this region directive :)
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

    private static (string binanceApiKey, string privateKeyPath) GetNormalExecutionEnvVars()
    {
        string binanceApiKey = ThrowIfMissingOrReturnValue(EnvKeys.BINANCE_API_KEY);
        string keyPath = ThrowIfMissingOrReturnValue(EnvKeys.BINANCE_ED25519_PRIVATE_KEY_PATH);
        return (binanceApiKey, keyPath);
    }

    private static async Task RunNormalExecutionLoop(ExecutionMode mode,
        BinanceFeedReader binanceFeedReader,
        BinanceLiveBootstrap bootstrap,
        Instrument[] instruments,
        InstrumentRuntime[] runtimes,
        AccountRiskLedger ledger,
        BinanceAccountStream accountStream,
        CancellationTokenSource cancellation)
    {
        bool first = true;

        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                ledger.Invalidate();
                await bootstrap.LoadAccount(
                    ledger,
                    runtimes,
                    cancellation.Token,
                    initializeInventory: first
                );
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

    #endregion NormalExecution

    #region Replay

    private static (string[] usedSymbols, InstrumentRuntime[] runtimes, bool syntheticReplay) GetFixture(
        string[] symbols)
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

        return (usedSymbols: fixture.Symbols, runtimes: fixture.Runtimes, syntheticReplay);
    }

    private static (ReplayBenchmarkRecorder? benchmark,
        int decisions,
        int readyDecisions,
        int actionable,
        long allocated,
        bool measure,
        string? benchmarkOutput)
        InitializeBenchmarks(BinanceFeedReader feed)
    {
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

        return (benchmark, decisions, readyDecisions, actionable, allocated, measure, benchmarkOutput);
    }

    private static void LogOutcome(int decisions,
        int readyDecisions,
        int actionable,
        long allocated,
        bool measure,
        InstrumentRuntime[] runtimes
    )
    {
        Console.WriteLine(
            $"Replay complete: {decisions} decisions, {actionable} with place/replace/cancel intents.");
        Console.WriteLine($"Products: {runtimes.Count(runtime => runtime.OrderBook.IsSynchronized)} " +
                          $"with a book, {runtimes.Count(runtime => runtime.Ready)} ready " +
                          $"of {runtimes.Length} configured.");
        if (measure) Console.WriteLine($"MARKET_ALLOCATED_BYTES={allocated};READY_DECISIONS={readyDecisions}");
    }

    #endregion Replay
}