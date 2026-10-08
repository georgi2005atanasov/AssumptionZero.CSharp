namespace AssumptionZero.Domain.InMemory;

public readonly record struct QuoteDecision(
    long BidPriceTicks,
    long BidQuantity,
    QuoteAction BidAction,
    long AskPriceTicks,
    long AskQuantity,
    QuoteAction AskAction)
{
    public bool QuoteBid => BidQuantity > 0;

    public bool QuoteAsk => AskQuantity > 0;
}