namespace AssumptionZero.Domain.InMemory;

public enum QuoteAction : byte
{
    None,
    Place,
    Keep,
    Replace,
    Cancel
}
