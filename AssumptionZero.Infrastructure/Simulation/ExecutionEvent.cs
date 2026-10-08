namespace AssumptionZero.Infrastructure.Simulation;

public sealed record ExecutionEvent(
    long Sequence,
    long CommandId,
    long Intent,
    int Instrument,
    bool Buy,
    long OrderId,
    string Kind,
    int Status,
    long Filled,
    long Price,
    string Reason,
    BalanceValue[] Balances,
    long ServerTicks);
