namespace AssumptionZero.Domain.InMemory;

public struct OrderBook(int trackedLevels)
{
    public BookLevel[] Bids { get; } = new BookLevel[trackedLevels];
    public BookLevel[] Asks { get; } = new BookLevel[trackedLevels];
}
