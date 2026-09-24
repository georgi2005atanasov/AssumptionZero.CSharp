using AssumptionZero.Infrastructure;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellation.Cancel();
};

string binanceApiKey = Environment.GetEnvironmentVariable("BINANCE_APIKEY") ??
                       "my-test-env-key";
string[] symbols =
[
    "BTCUSDT", "ETHUSDT", "BNBUSDT", "SOLUSDT", "XRPUSDT",
    "ADAUSDT", "DOGEUSDT", "AVAXUSDT", "LINKUSDT", "DOTUSDT",
    "LTCUSDT", "BCHUSDT", "TRXUSDT", "XLMUSDT", "UNIUSDT",
    "ATOMUSDT", "NEARUSDT", "ETCUSDT", "APTUSDT", "FILUSDT",
    "SUIUSDT", "ARBUSDT", "OPUSDT", "INJUSDT", "ICPUSDT",
    "SHIBUSDT", "PEPEUSDT", "WIFUSDT", "RENDERUSDT", "POLUSDT",
    "AAVEUSDT", "FETUSDT", "RUNEUSDT", "SEIUSDT", "TIAUSDT",
    "ALGOUSDT", "HBARUSDT", "VETUSDT", "JUPUSDT", "0GUSDT"
];
var binanceFeedReader = new BinanceFeedReader(
    binanceApiKey,
    symbols,
    baseUrl: "wss://stream-sbe.testnet.binance.vision/stream?streams="
);

binanceFeedReader.QuoteReceived += quote =>
{
    Console.WriteLine(
        $"QUOTE {quote.Symbol,-12} " +
        $"BID {quote.BidPrice} ({quote.BidQuantity}) | " +
        $"ASK {quote.AskPrice} ({quote.AskQuantity}) | " +
        $"UpdateId={quote.UpdateId}");
};

try
{
    await binanceFeedReader.Start(cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("Stopped.");
}