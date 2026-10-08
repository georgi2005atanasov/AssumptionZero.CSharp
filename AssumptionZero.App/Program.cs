using AssumptionZero.Application.Interfaces.Services;
using AssumptionZero.App;
using AssumptionZero.Infrastructure;

if (Environment.GetEnvironmentVariable("EXECUTION_MODE") == "SimulatedExchange")
{
    using var simStop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        simStop.Cancel();
    };
    IClosedLoopClient client = new ClosedLoopClient();
    await client.Run(
        Environment.GetEnvironmentVariable("SIM_EXCHANGE_URL")
            ?? throw new ArgumentException("SIM_EXCHANGE_URL required"),
        Environment.GetEnvironmentVariable("SIM_OUTPUT")
            ?? throw new ArgumentException("SIM_OUTPUT required"),
        simStop.Token);
    return;
}

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
    IExecutionRouter router = new ExecutionRouter();
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
