namespace AssumptionZero.Infrastructure.External;

internal readonly record struct InstrumentFields(
    string Symbol,
    string BaseAsset,
    string QuoteAsset,
    decimal TickSize,
    decimal QuantityStepSize,
    decimal MinQuantity,
    decimal MaxQuantity,
    decimal MinNotional,
    decimal MinPrice,
    decimal MaxPrice);
