using AssumptionZero.Domain.InMemory;

namespace AssumptionZero.Infrastructure.External;

internal struct AccountExecutionFields
{
    public AccountEventKind Kind = AccountEventKind.Invalidate;
    public int RuntimeIndex;
    public bool Buy;
    public long OrderId;
    public long TimeMs;
    public decimal Filled;
    public decimal FillPrice;
    public OrderStatus? Status;

    public AccountExecutionFields()
    {
    }
}
