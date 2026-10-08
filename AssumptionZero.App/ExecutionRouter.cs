using AssumptionZero.Application.Interfaces.External;
using AssumptionZero.Application.Interfaces.Services;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.Entities;
using AssumptionZero.Domain.InMemory;
using AssumptionZero.Infrastructure.Benchmarking;
using AssumptionZero.Infrastructure.External;

namespace AssumptionZero.App;

public sealed class ExecutionRouter : IExecutionRouter
{

    public async Task SeedData(
        string apiKey,
        string connectionString,
        string[] symbols,
        bool logEvents,
        CancellationToken cancellationToken)
    {
        IDataFeedReader feed = new BinanceFeedReader(apiKey, symbols, AppConstants.BaseSbeStreamUrl,
            FeedMode.Capture, logEvents);
        await feed.Start(cancellationToken, connectionString);
    }

    public async Task NormalExecution(string[] symbols, bool logEvents, CancellationToken cancellationToken)
    {
        string apiKey = ThrowIfMissing(EnvKeys.BINANCE_API_KEY);
        string privateKeyPath = ThrowIfMissing(EnvKeys.BINANCE_ED25519_PRIVATE_KEY_PATH);
        using ILiveBootstrap bootstrap = new BinanceLiveBootstrap(apiKey, privateKeyPath);
        Instrument[] instruments = await bootstrap.LoadInstruments(symbols, cancellationToken);
        string[] assets = instruments.SelectMany(i => new[] { i.BaseAsset,
            i.QuoteAsset }).Distinct(StringComparer.Ordinal).ToArray();
        var ledger = new AccountRiskLedger(assets);
        HotConfig config = new();
        InstrumentRuntime[] runtimes = instruments.Select(i => new InstrumentRuntime(i, config, ledger, 0,
            config.BaseQuantity * 10)).ToArray();
        await using IAccountStream accountStream = new BinanceAccountStream(apiKey, bootstrap, ledger, runtimes);
        await accountStream.Start(cancellationToken);
        IDataFeedReader feed = new BinanceFeedReader(
            apiKey,
            instruments.Select(i => i.Symbol).ToArray(),
            AppConstants.BaseSbeStreamUrl,
            FeedMode.Live,
            logEvents,
            runtimes,
            accountStream);
        var session = new LiveExecutionSession(bootstrap, ledger, runtimes, accountStream, feed);
        await session.Run(cancellationToken);
    }

    public async Task Replay(string[] symbols, bool logEvents, CancellationToken cancellationToken)
    {
        string? fixturePath = EnvironmentVariables.Get(EnvKeys.REPLAY_FIXTURE);
        bool estimateBook = string.IsNullOrWhiteSpace(fixturePath);
        HotConfig config = new();
        var state = estimateBook ? EstimatedReplayState.Create(symbols,
            config) : FixtureReplayState.Load(fixturePath!, config);
        if (estimateBook)
        {
            Console.WriteLine(
                "Performance replay: account and starting books are estimated; quote values are not historical results.");
        }

        var reader = new BinanceFeedReader(
            string.Empty,
            state.Symbols,
            EnvironmentVariables.Get(EnvKeys.REPLAY_URL) ?? AppConstants.DefaultReplayUrl,
            estimateBook ? FeedMode.ReplayWithEstimatedBook : FeedMode.ReplayWithSnapshot,
            logEvents,
            state.Runtimes);
        string? benchmarkOutput = EnvironmentVariables.Get(EnvKeys.BENCHMARK_OUTPUT);
        using ReplayBenchmarkRecorder? benchmark = CreateBenchmark(benchmarkOutput);
        reader.Benchmark = benchmark;
        string? tracePath = EnvironmentVariables.Get(EnvKeys.CORRECTNESS_TRACE);
        using StreamWriter? trace = tracePath is null ? null : new StreamWriter(tracePath);
        var observer = new ReplayDecisionObserver(trace, EnvironmentVariables.Get(EnvKeys.MEASURE_ALLOCATIONS) == "1");
        reader.DecisionProduced += observer.RecordDecision;
        await RunReplay(reader, state.Runtimes, benchmark, benchmarkOutput, cancellationToken);
        observer.WriteSummary(state.Runtimes);
    }

    private static ReplayBenchmarkRecorder? CreateBenchmark(string? output)
    {
        if (output is null)
        {
            return null;
        }

        return new ReplayBenchmarkRecorder(
            int.Parse(EnvironmentVariables.Get(EnvKeys.BENCHMARK_EXPECTED_FRAMES)
                ?? throw new InvalidOperationException("BENCHMARK_EXPECTED_FRAMES is required.")),
            int.Parse(EnvironmentVariables.Get(EnvKeys.BENCHMARK_WARMUP_FRAMES) ?? "0"),
            EnvironmentVariables.Get(EnvKeys.BENCHMARK_DIAGNOSTIC) == "1");
    }

    private static async Task RunReplay(
        IDataFeedReader feed,
        InstrumentRuntime[] runtimes,
        ReplayBenchmarkRecorder? benchmark,
        string? output,
        CancellationToken cancellationToken)
    {
        bool completed = false;
        try
        {
            await feed.Start(cancellationToken);
            completed = true;
        }
        finally
        {
            if (benchmark is not null)
            {
                (benchmark.Finish(completed)with
                {
                    FinalStateHash = DiscreteStateHash.Compute(runtimes)
                }

                ).Write(output!);
            }
        }
    }

    private static string ThrowIfMissing(EnvKeys key) =>
        EnvironmentVariables.Get(key) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{key} not found.");
}
