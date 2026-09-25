using AssumptionZero.Infrastructure;
using AssumptionZero.Infrastructure.External;

string binanceApiKey = Environment.GetEnvironmentVariable("BINANCE_APIKEY") ??
                       "my-test-env-key";
string connectionString = Environment.GetEnvironmentVariable("PG_CONNECTION") ??
                          "Host=172.17.0.2;Database=assumption_zero;Username=postgres;Password=mysecretpassword;Trust Server Certificate=true;";

string executionMode = Environment.GetEnvironmentVariable("EXECUTION_MODE") ??
                       "SeedData";
if (!Enum.TryParse(executionMode, ignoreCase: true, out ExecutionMode mode) || !Enum.IsDefined(mode))
    throw new ArgumentException("EXECUTION_MODE must be SeedData, NormalExecution, or Replay.");
bool replay = mode == ExecutionMode.Replay;
string? logSetting = Environment.GetEnvironmentVariable("LOG_EVENTS");
bool logEvents = logSetting is null ? !replay : bool.Parse(logSetting);

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
var binanceFeedReader = new BinanceFeedReader(
    binanceApiKey,
    symbols,
    baseUrl: replay
        ? Environment.GetEnvironmentVariable("REPLAY_URL") ?? "ws://127.0.0.1:5080/stream"
        : "wss://stream-sbe.testnet.binance.vision/stream?streams=",
    replay: replay,
    logEvents: logEvents
);

binanceFeedReader.QuoteReceived += quote =>
{
    if (!logEvents) return;
    Console.WriteLine(
        $"QUOTE {quote.Symbol,-12} " +
        $"BID {quote.BidPrice} ({quote.BidQuantity}) | " +
        $"ASK {quote.AskPrice} ({quote.AskQuantity}) | " +
        $"UpdateId={quote.UpdateId}");
};

try
{
    switch (mode)
    {
        case ExecutionMode.SeedData:
            // var pgClient = new NpgsqlClient(connectionString);
            // var projectDirectory = Directory.GetParent(AppContext.BaseDirectory)!
            //     .Parent!
            //     .Parent!
            //     .Parent!
            //     .FullName;
            //
            // var initSqlPath = Path.Combine(projectDirectory, "init.sql");
            // var initSql = File.ReadAllText(initSqlPath);
            // await pgClient.ExecuteAsync(initSql);
            await binanceFeedReader.Start(cancellation.Token, mode, connectionString);
            break;
        case ExecutionMode.NormalExecution:
        case ExecutionMode.Replay:
            await binanceFeedReader.Start(cancellation.Token, mode);
            break;
    }
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("Stopped.");
}
