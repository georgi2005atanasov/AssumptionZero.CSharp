using AssumptionZero.App;
using AssumptionZero.Infrastructure;

string binanceApiKey = EnvironmentVariables.Get(EnvKeys.BINANCE_API_KEY) ??
                       AssumptionZero.App.AppConstants.DefaultBinanceApiKey;
string connectionString = EnvironmentVariables.Get(EnvKeys.PG_CONNECTION) ??
                          AssumptionZero.App.AppConstants.DefaultPgConnectionString;
string executionMode = EnvironmentVariables.Get(EnvKeys.EXECUTION_MODE) ??
                       nameof(ExecutionMode.Replay);

if (!Enum.TryParse(executionMode, ignoreCase: true, out ExecutionMode mode) || !Enum.IsDefined(mode))
    throw new ArgumentException("EXECUTION_MODE must be SeedData, NormalExecution, or Replay.");
string? logSetting = EnvironmentVariables.Get(EnvKeys.LOG_EVENTS);
bool logEvents = logSetting is not null && bool.Parse(logSetting);

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};

// In a more realistic manner, these symbols could have been fetched form the db
string[] symbols =
[
    "BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "XRPUSDT"
];

try
{
    var router = new ExecutionRouter();
    switch (mode)
    {
        case ExecutionMode.SeedData:
            await router.SeedData(
                binanceApiKey,
                connectionString,
                symbols,
                logEvents,
                cancellation.Token
            );
            break;
        case ExecutionMode.NormalExecution:
        {
            await router.NormalExecution(
                symbols,
                logEvents,
                cancellation.Token
            );
            break;
        }
        case ExecutionMode.Replay:
        {
            await router.Replay(
                symbols,
                logEvents,
                cancellation.Token
            );
            break;
        }
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("Stopped.");
}
