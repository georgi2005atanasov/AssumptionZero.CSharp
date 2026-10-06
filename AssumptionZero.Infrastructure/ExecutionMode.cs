namespace AssumptionZero.Infrastructure;

public enum ExecutionMode
{
    /// <summary>Capture live market messages in the database.</summary>
    SeedData,

    /// <summary>Process live Binance market and account messages.</summary>
    NormalExecution,

    /// <summary>Process a market recording served by the local simulator.</summary>
    Replay,
}