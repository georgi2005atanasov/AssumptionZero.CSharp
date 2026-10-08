namespace AssumptionZero.Infrastructure.External;

public readonly record struct BestQuote(
    string Symbol,
    decimal BidPrice,
    decimal BidQuantity,
    decimal AskPrice,
    decimal AskQuantity,
    long UpdateId,
    long EventTimeMicros);
