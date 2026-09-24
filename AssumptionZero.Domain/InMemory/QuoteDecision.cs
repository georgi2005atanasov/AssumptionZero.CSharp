namespace AssumptionZero.Domain.InMemory;

public readonly struct QuoteDecision
{
    private readonly long BidPriceTicks;
    public readonly long BidQuantity;

    public readonly long AskPriceTicks;
    public readonly long AskQuantity;

    public readonly bool QuoteBid;
    public readonly bool QuoteAsk;
}