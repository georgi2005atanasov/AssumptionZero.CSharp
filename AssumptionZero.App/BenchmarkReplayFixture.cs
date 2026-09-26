using AssumptionZero.Application.Services;
using AssumptionZero.Domain.Entities;
using AssumptionZero.Domain.InMemory;

namespace AssumptionZero.App;

/// <summary>Deterministic, invented account and filters for market-only performance recordings.</summary>
internal static class BenchmarkReplayFixture
{
    // Placeholder units; the first SBE quote/depth for each symbol supplies its own exponents.
    private const decimal Grid = 1m;

    public static (string[] Symbols, InstrumentRuntime[] Runtimes) Create(
        string[] symbols, in HotConfig config)
    {
        if (symbols.Length == 0) throw new ArgumentException("No replay symbols were configured.", nameof(symbols));
        var assets = new string[symbols.Length + 1];
        assets[0] = "USDT";
        for (int i = 0; i < symbols.Length; i++)
        {
            if (!symbols[i].EndsWith("USDT", StringComparison.Ordinal))
                throw new ArgumentException("Benchmark replay requires USDT pairs.", nameof(symbols));
            assets[i + 1] = symbols[i][..^4];
        }

        var ledger = new AccountRiskLedger(assets);
        ledger.BeginSnapshot(0.001m);
        ledger.SetBalance(0, 1_000_000_000m, 0);
        for (int i = 0; i < symbols.Length; i++) ledger.SetBalance(i + 1, 100m, 0);
        ledger.CompleteSnapshot();

        var runtimes = new InstrumentRuntime[symbols.Length];
        for (int i = 0; i < symbols.Length; i++)
        {
            var instrument = new Instrument(i, symbols[i], assets[i + 1], "USDT",
                Grid, Grid, Grid, 1_000_000m, 0);
            long target = instrument.ToQuantityUnits(100m);
            long maxLong = checked(target + config.BaseQuantity * 10);
            var runtime = new InstrumentRuntime(instrument, config, ledger, target, maxLong,
                syntheticReplay: true);
            runtime.OnBalanceChanged();
            runtimes[i] = runtime;
        }
        return (symbols, runtimes);
    }
}
