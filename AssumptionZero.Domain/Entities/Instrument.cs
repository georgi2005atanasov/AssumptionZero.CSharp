namespace AssumptionZero.Domain.Entities;

public class Instrument
{
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
}