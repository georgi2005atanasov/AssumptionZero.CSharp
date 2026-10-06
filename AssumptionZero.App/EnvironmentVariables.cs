// ReSharper disable InconsistentNaming
namespace AssumptionZero.App;

public enum EnvKeys
{
    BINANCE_API_KEY,
    PG_CONNECTION,
    EXECUTION_MODE,
    LOG_EVENTS,
    BINANCE_ED25519_PRIVATE_KEY_PATH,
    REPLAY_FIXTURE,
    REPLAY_URL,
    BENCHMARK_OUTPUT,
    MEASURE_ALLOCATIONS,
    BENCHMARK_EXPECTED_FRAMES,
    BENCHMARK_WARMUP_FRAMES,
    BENCHMARK_DIAGNOSTIC,
    CORRECTNESS_TRACE,
}

public static class EnvironmentVariables
{
    public static string? Get(EnvKeys key)
        => Environment.GetEnvironmentVariable(key.ToString());
}