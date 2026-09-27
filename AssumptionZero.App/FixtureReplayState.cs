using System.Globalization;
using System.Text.Json;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.Entities;
using AssumptionZero.Domain.InMemory;

namespace AssumptionZero.App;

internal static class FixtureReplayState
{
    public static (string[] Symbols, InstrumentRuntime[] Runtimes) Load(string path, in HotConfig config)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        JsonElement balances = root.GetProperty("balances");
        string[] assets = balances.EnumerateArray()
            .Select(item => item.GetProperty("asset").GetString()!).ToArray();
        var ledger = new AccountRiskLedger(assets);
        ledger.BeginSnapshot(Dec(root.GetProperty("makerFeeRate")));
        int asset = 0;
        foreach (JsonElement balance in balances.EnumerateArray())
            ledger.SetBalance(asset++, Dec(balance.GetProperty("free")), Dec(balance.GetProperty("locked")));
        ledger.CompleteSnapshot();

        var instruments = root.GetProperty("instruments");
        var runtimes = new InstrumentRuntime[instruments.GetArrayLength()];
        var symbols = new string[runtimes.Length];
        int index = 0;
        foreach (JsonElement item in instruments.EnumerateArray())
        {
            var instrument = new Instrument(index, item.GetProperty("symbol").GetString()!,
                item.GetProperty("baseAsset").GetString()!, item.GetProperty("quoteAsset").GetString()!,
                Dec(item.GetProperty("tickSize")), Dec(item.GetProperty("stepSize")),
                Dec(item.GetProperty("minQuantity")), Dec(item.GetProperty("maxQuantity")),
                Dec(item.GetProperty("minNotional")),
                item.TryGetProperty("minPrice", out JsonElement minPrice) ? Dec(minPrice) : 0,
                item.TryGetProperty("maxPrice", out JsonElement maxPrice) ? Dec(maxPrice) : decimal.MaxValue);
            long target = instrument.ToQuantityUnits(Dec(item.GetProperty("targetQuantity")));
            long maxLong = instrument.ToQuantityUnits(Dec(item.GetProperty("maxLongQuantity")));
            var runtime = new InstrumentRuntime(instrument, config, ledger, target, maxLong);
            runtime.OnBalanceChanged();
            JsonElement snapshot = item.GetProperty("snapshot");
            BookLevel[] bids = Levels(instrument, snapshot.GetProperty("bids"));
            BookLevel[] asks = Levels(instrument, snapshot.GetProperty("asks"));
            if (!runtime.LoadSnapshot(bids, asks, snapshot.GetProperty("lastUpdateId").GetInt64()))
                throw new InvalidDataException($"Invalid replay snapshot for {instrument.Symbol}.");
            runtimes[index] = runtime;
            symbols[index++] = instrument.Symbol;
        }
        if (runtimes.Length == 0) throw new InvalidDataException("Replay fixture has no instruments.");
        return (symbols, runtimes);
    }

    private static BookLevel[] Levels(Instrument instrument, JsonElement levels)
    {
        var result = new BookLevel[levels.GetArrayLength()];
        int i = 0;
        foreach (JsonElement level in levels.EnumerateArray())
        {
            decimal price = Dec(level[0]), quantity = Dec(level[1]);
            long ticks = instrument.ToPriceTicks(price), units = instrument.ToQuantityUnits(quantity);
            if (instrument.Price(ticks) != price || instrument.Quantity(units) != quantity)
                throw new InvalidDataException("Replay level is off the instrument grid.");
            result[i++] = new BookLevel { PriceTicks = ticks, Quantity = units };
        }
        return result;
    }

    private static decimal Dec(JsonElement element) =>
        decimal.Parse(element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText(),
            CultureInfo.InvariantCulture);
}
