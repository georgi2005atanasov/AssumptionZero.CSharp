using System.Diagnostics.CodeAnalysis;

namespace AssumptionZero.Domain.Entities;

public class Instrument
{
    [SetsRequiredMembers]
    public Instrument(int id, string symbol, string baseAsset, string quoteAsset, decimal tickSize,
        decimal quantityStepSize, decimal minQuantity, decimal maxQuantity, decimal minNotional,
        decimal minPrice = 0, decimal maxPrice = decimal.MaxValue)
    {
        if (tickSize <= 0 || quantityStepSize <= 0 || minQuantity < 0 || maxQuantity < minQuantity ||
            minNotional < 0 || minPrice < 0 || maxPrice < minPrice)
            throw new ArgumentOutOfRangeException(nameof(tickSize), "Invalid instrument filters.");
        Id = id;
        Symbol = symbol;
        BaseAsset = baseAsset;
        QuoteAsset = quoteAsset;
        TickSize = tickSize;
        QuantityStepSize = quantityStepSize;
        MinQuantity = minQuantity;
        MaxQuantity = maxQuantity;
        MinNotional = minNotional;
        MinPrice = minPrice;
        MaxPrice = maxPrice;
    }

    public int Id { get; init; }

    /// <summary>
    /// BTCUSDT
    /// </summary>
    public required string Symbol { get; init; }

    /// <summary>
    /// BTC
    /// </summary>
    public required string BaseAsset { get; init; }

    /// <summary>
    /// USDT
    /// </summary>
    public required string QuoteAsset { get; init; }

    public decimal TickSize { get; private set; }

    public decimal QuantityStepSize { get; private set; }

    public decimal MinQuantity { get; private set; }

    public decimal MaxQuantity { get; private set; }

    /// <summary>
    /// Price * Quantity = Notional
    /// For example:
    /// Price = 68,000 USDT
    /// Quantity = 0.0001 BTC
    /// 68,000×0.0001=6.8 USDT
    /// </summary>
    public decimal MinNotional { get; private set; }

    public decimal MinPrice { get; private set; }
    public decimal MaxPrice { get; private set; }

    public long ToPriceTicks(decimal price) => checked((long)(price / TickSize));

    public long ToQuantityUnits(decimal quantity) => checked((long)(quantity / QuantityStepSize));

    public decimal Price(long ticks) => ticks * TickSize;

    public decimal Quantity(long units) => units * QuantityStepSize;
}