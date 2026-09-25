namespace AssumptionZero.Infrastructure;

public enum ExecutionMode
{
    /// <summary>
    /// In this case, we ensure we fill our database in order for us to be able
    /// to backtest later and to get data for comparison reasons.
    /// </summary>
    SeedData,

    /// <summary>
    /// Running for recording the performance.
    /// </summary>
    NormalExecution,

    /// <summary>
    /// Processes a previously recorded Binance SBE stream served by the local simulator.
    /// Uses the normal receive and decode path without recording the messages again.
    /// A normal WebSocket close means the replay has finished.
    /// </summary>
    Replay,
}
