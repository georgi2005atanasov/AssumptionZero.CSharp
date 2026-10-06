using System.Globalization;
using System.Text.Json;
using AssumptionZero.Domain.Entities;

namespace AssumptionZero.Infrastructure.External;

internal static class InstrumentExtensions
{
    public static InstrumentFields? ExtractFields(this JsonElement symbol)
    {
        decimal tickSize = 0, quantityStepSize = 0, minQuantity = 0, maxQuantity = 0, minNotional = 0;
        decimal minPrice = 0, maxPrice = decimal.MaxValue;

        foreach (JsonElement filter in symbol.GetProperty("filters").EnumerateArray())
        {
            switch (filter.GetProperty("filterType").GetString())
            {
                case "PRICE_FILTER":
                    tickSize = decimal.Parse(filter.GetProperty("tickSize").GetString()!,
                        CultureInfo.InvariantCulture);
                    minPrice = decimal.Parse(filter.GetProperty("minPrice").GetString()!,
                        CultureInfo.InvariantCulture);
                    maxPrice = decimal.Parse(filter.GetProperty("maxPrice").GetString()!,
                        CultureInfo.InvariantCulture);
                    if (maxPrice == 0) maxPrice = decimal.MaxValue;
                    break;
                case "LOT_SIZE":
                    quantityStepSize = decimal.Parse(filter.GetProperty("stepSize").GetString()!,
                        CultureInfo.InvariantCulture);
                    minQuantity = decimal.Parse(filter.GetProperty("minQty").GetString()!,
                        CultureInfo.InvariantCulture);
                    maxQuantity = decimal.Parse(filter.GetProperty("maxQty").GetString()!,
                        CultureInfo.InvariantCulture);
                    break;
                case "MIN_NOTIONAL":
                case "NOTIONAL":
                    minNotional = decimal.Parse(filter.GetProperty("minNotional").GetString()!,
                        CultureInfo.InvariantCulture);
                    break;
            }
        }

        if (tickSize <= 0 || quantityStepSize <= 0 || maxQuantity <= 0) return null;

        return new InstrumentFields(
            symbol.GetProperty("symbol").GetString()!,
            symbol.GetProperty("baseAsset").GetString()!,
            symbol.GetProperty("quoteAsset").GetString()!,
            tickSize, quantityStepSize, minQuantity, maxQuantity, minNotional, minPrice, maxPrice);
    }

    public static Instrument? ThenBuild(this InstrumentFields? fields, int id)
    {
        if (fields is not { } value) return null;

        return new Instrument(id, value.Symbol, value.BaseAsset, value.QuoteAsset,
            value.TickSize, value.QuantityStepSize, value.MinQuantity, value.MaxQuantity,
            value.MinNotional, value.MinPrice, value.MaxPrice);
    }
}

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
