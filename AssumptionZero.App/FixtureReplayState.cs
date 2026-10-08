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
        AccountRiskLedger ledger = LoadAccount(root);
        var instruments = root.GetProperty("instruments");
        var runtimes = new InstrumentRuntime[instruments.GetArrayLength()];
        var symbols = new string[runtimes.Length];
        int index = 0;
        foreach (JsonElement item in instruments.EnumerateArray())
        {
            InstrumentRuntime runtime = LoadRuntime(item, index, config, ledger);
            Instrument instrument = runtime.Instrument;
            runtimes[index] = runtime;
            symbols[index++] = instrument.Symbol;
        }

        if (runtimes.Length == 0)
        {
            throw new InvalidDataException("Replay fixture has no instruments.");
        }

        return (symbols, runtimes);
    }

    private static AccountRiskLedger LoadAccount(JsonElement root)
    {
        JsonElement balances = root.GetProperty("balances");
        string[] assets = balances.EnumerateArray().Select(item => item.GetProperty("asset").GetString()!).ToArray();
        var ledger = new AccountRiskLedger(assets);
        ledger.BeginSnapshot(ReadDecimal(root.GetProperty("makerFeeRate")));
        int asset = 0;
        foreach (JsonElement balance in balances.EnumerateArray())
        {
            ledger.SetBalance(asset++, ReadDecimal(balance.GetProperty("free")),
                ReadDecimal(balance.GetProperty("locked")));
        }

        ledger.CompleteSnapshot();
        return ledger;
    }

    private static InstrumentRuntime LoadRuntime(JsonElement item, int index, in HotConfig config,
        AccountRiskLedger ledger)
    {
        Instrument instrument = ReadInstrument(item, index);
        long target = instrument.ToQuantityUnits(ReadDecimal(item.GetProperty("targetQuantity")));
        long maxLong = instrument.ToQuantityUnits(ReadDecimal(item.GetProperty("maxLongQuantity")));
        var runtime = new InstrumentRuntime(instrument, config, ledger, target, maxLong);
        runtime.OnBalanceChanged();
        LoadSnapshot(item.GetProperty("snapshot"), runtime);
        return runtime;
    }

    private static Instrument ReadInstrument(JsonElement item, int index)
    {
        return new Instrument(
            index,
            item.GetProperty("symbol").GetString()!,
            item.GetProperty("baseAsset").GetString()!,
            item.GetProperty("quoteAsset").GetString()!,
            ReadDecimal(item.GetProperty("tickSize")),
            ReadDecimal(item.GetProperty("stepSize")),
            ReadDecimal(item.GetProperty("minQuantity")),
            ReadDecimal(item.GetProperty("maxQuantity")),
            ReadDecimal(item.GetProperty("minNotional")),
            item.TryGetProperty("minPrice", out JsonElement minPrice) ? ReadDecimal(minPrice) : 0,
            item.TryGetProperty("maxPrice", out JsonElement maxPrice) ? ReadDecimal(maxPrice) : decimal.MaxValue);
    }

    private static void LoadSnapshot(JsonElement snapshot, InstrumentRuntime runtime)
    {
        Instrument instrument = runtime.Instrument;
        BookLevel[] bids = ReadLevels(instrument, snapshot.GetProperty("bids"));
        BookLevel[] asks = ReadLevels(instrument, snapshot.GetProperty("asks"));
        if (!runtime.LoadSnapshot(bids, asks, snapshot.GetProperty("lastUpdateId").GetInt64()))
        {
            throw new InvalidDataException($"Invalid replay snapshot for {instrument.Symbol}.");
        }
    }

    private static BookLevel[] ReadLevels(Instrument instrument, JsonElement levels)
    {
        var result = new BookLevel[levels.GetArrayLength()];
        int i = 0;
        foreach (JsonElement level in levels.EnumerateArray())
        {
            decimal price = ReadDecimal(level[0]), quantity = ReadDecimal(level[1]);
            long ticks = instrument.ToPriceTicks(price), units = instrument.ToQuantityUnits(quantity);
            if (instrument.Price(ticks) != price || instrument.Quantity(units) != quantity)
            {
                throw new InvalidDataException("Replay level is off the instrument grid.");
            }

            result[i++] = new BookLevel
            {
                PriceTicks = ticks,
                Quantity = units
            };
        }

        return result;
    }

    private static decimal ReadDecimal(JsonElement element) =>
        decimal.Parse(
            element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText(),
            CultureInfo.InvariantCulture);
}
