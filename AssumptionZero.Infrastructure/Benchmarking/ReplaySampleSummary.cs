namespace AssumptionZero.Infrastructure.Benchmarking;

internal readonly record struct ReplaySampleSummary(
    int MeasuredCount,
    long[] Processing,
    long[] ReceiveToDecision,
    int WithDecision,
    int MeasuredDecisions,
    long MeasuredBytes);
