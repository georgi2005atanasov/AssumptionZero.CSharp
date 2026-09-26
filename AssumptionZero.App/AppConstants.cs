namespace AssumptionZero.App;

public static class AppConstants
{
    /// <summary>
    /// For the sake of testing, I would just put it here.
    /// </summary>
    public const string DefaultPgConnectionString = "Host=172.17.0.2;Database=assumption_zero;Username=postgres;Password=mysecretpassword;Trust Server Certificate=true;";

    /// <summary>
    /// Never put real key here - this key is for testing purposes
    /// </summary>
    public const string DefaultBinanceApiKey = "my-test-env-key";

    public const string BaseSbeStreamUrl = "wss://stream-sbe.testnet.binance.vision/stream?streams=";

    public const string DefaultReplayUrl = "ws://127.0.0.1:5080/stream";
}