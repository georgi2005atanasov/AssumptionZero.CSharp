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
                    tickSize = ReadFilterDecimal(filter, "tickSize");
                    minPrice = ReadFilterDecimal(filter, "minPrice");
                    maxPrice = ReadFilterDecimal(filter, "maxPrice");
                    if (maxPrice == 0)
                    {
                        maxPrice = decimal.MaxValue;
                    }

                    break;
                case "LOT_SIZE":
                    quantityStepSize = ReadFilterDecimal(filter, "stepSize");
                    minQuantity = ReadFilterDecimal(filter, "minQty");
                    maxQuantity = ReadFilterDecimal(filter, "maxQty");
                    break;
                case "MIN_NOTIONAL":
                case "NOTIONAL":
                    minNotional = ReadFilterDecimal(filter, "minNotional");
                    break;
            }
        }

        if (tickSize <= 0 || quantityStepSize <= 0 || maxQuantity <= 0)
        {
            return null;
        }

        return new InstrumentFields(
            symbol.GetProperty("symbol").GetString()!,
            symbol.GetProperty("baseAsset").GetString()!,
            symbol.GetProperty("quoteAsset").GetString()!,
            tickSize,
            quantityStepSize,
            minQuantity,
            maxQuantity,
            minNotional,
            minPrice,
            maxPrice);
    }

    private static decimal ReadFilterDecimal(JsonElement filter, string property) =>
        decimal.Parse(filter.GetProperty(property).GetString()!, CultureInfo.InvariantCulture);

    public static Instrument? ThenBuild(this InstrumentFields? fields, int id)
    {
        if (fields is not { } value)
        {
            return null;
        }

        return new Instrument(
            id,
            value.Symbol,
            value.BaseAsset,
            value.QuoteAsset,
            value.TickSize,
            value.QuantityStepSize,
            value.MinQuantity,
            value.MaxQuantity,
            value.MinNotional,
            value.MinPrice,
            value.MaxPrice);
    }
}
