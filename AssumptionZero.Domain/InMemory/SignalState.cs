namespace AssumptionZero.Domain.InMemory;

/// <summary>
/// What does the things from signal memory tell me?
/// stores the final scores from SignalMemory
/// </summary>
public struct SignalState
{
    public double MicroSignal;

    public double MultiLevelImbalance;
    public double OrderFlowImbalance;
    public double TradeFlowImbalance;

    public double MomentumSignal;
    public double VolatilityTicks;

    public long LiquidityUnits;
    public double LiquidityRatio;
}