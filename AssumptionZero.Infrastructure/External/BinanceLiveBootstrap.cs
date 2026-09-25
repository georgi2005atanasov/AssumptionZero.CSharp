using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.Entities;
using AssumptionZero.Domain.InMemory;

namespace AssumptionZero.Infrastructure.External;

/// <summary>Read-only Spot Testnet startup data. All HTTP and signing work is outside the event path.</summary>
public sealed class BinanceLiveBootstrap : IDisposable
{
    private readonly HttpClient http = new() { BaseAddress = new Uri("https://testnet.binance.vision") };
    private readonly string apiKey;
    private readonly string privateKeyPath;

    public BinanceLiveBootstrap(string apiKey, string privateKeyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!File.Exists(privateKeyPath)) throw new FileNotFoundException("Ed25519 private key not found.", privateKeyPath);
        this.apiKey = apiKey;
        this.privateKeyPath = privateKeyPath;
    }

    public async Task<Instrument[]> LoadInstrumentsAsync(string[] wanted, CancellationToken cancellationToken)
    {
        using JsonDocument document = await GetAsync("/api/v3/exchangeInfo", false, cancellationToken);
        var result = new List<Instrument>(wanted.Length);
        foreach (JsonElement symbol in document.RootElement.GetProperty("symbols").EnumerateArray())
        {
            string name = symbol.GetProperty("symbol").GetString()!;
            if (!wanted.Contains(name, StringComparer.Ordinal)) continue;
            if (symbol.GetProperty("status").GetString() != "TRADING") continue;
            decimal tick = 0, step = 0, minQty = 0, maxQty = 0, minNotional = 0;
            decimal minPrice = 0, maxPrice = decimal.MaxValue;
            foreach (JsonElement filter in symbol.GetProperty("filters").EnumerateArray())
            {
                switch (filter.GetProperty("filterType").GetString())
                {
                    case "PRICE_FILTER":
                        tick = decimal.Parse(filter.GetProperty("tickSize").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                        minPrice = decimal.Parse(filter.GetProperty("minPrice").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                        maxPrice = decimal.Parse(filter.GetProperty("maxPrice").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                        if (maxPrice == 0) maxPrice = decimal.MaxValue;
                        break;
                    case "LOT_SIZE":
                        step = decimal.Parse(filter.GetProperty("stepSize").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                        minQty = decimal.Parse(filter.GetProperty("minQty").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                        maxQty = decimal.Parse(filter.GetProperty("maxQty").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "MIN_NOTIONAL":
                    case "NOTIONAL":
                        minNotional = decimal.Parse(filter.GetProperty("minNotional").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                }
            }
            if (tick <= 0 || step <= 0 || maxQty <= 0) continue;
            result.Add(new Instrument(result.Count, name,
                symbol.GetProperty("baseAsset").GetString()!, symbol.GetProperty("quoteAsset").GetString()!,
                tick, step, minQty, maxQty, minNotional, minPrice, maxPrice));
        }
        if (result.Count == 0) throw new InvalidOperationException("None of the configured symbols are available on Spot Testnet.");
        return result.ToArray();
    }

    public async Task LoadAccountAsync(AccountRiskLedger ledger, InstrumentRuntime[] runtimes,
        CancellationToken cancellationToken, bool initializeInventory = false)
    {
        using JsonDocument account = await GetAsync("/api/v3/account", true, cancellationToken);
        JsonElement root = account.RootElement;
        decimal makerFee = decimal.Parse(root.GetProperty("commissionRates").GetProperty("maker").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
        ledger.BeginSnapshot(makerFee);
        foreach (InstrumentRuntime runtime in runtimes) runtime.BeginAccountReconciliation();
        foreach (JsonElement balance in root.GetProperty("balances").EnumerateArray())
        {
            int id = ledger.FindAsset(balance.GetProperty("asset").GetString()!);
            if (id < 0) continue;
            ledger.SetBalance(id,
                decimal.Parse(balance.GetProperty("free").GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                decimal.Parse(balance.GetProperty("locked").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
        }
        foreach (InstrumentRuntime runtime in runtimes)
        {
            runtime.OnBalanceChanged();
            if (initializeInventory)
            {
                long current = runtime.Inventory.CurrentQuantity;
                runtime.ConfigureInventory(current, checked(current + runtime.Config.BaseQuantity * 10));
            }
        }

        using JsonDocument openOrders = await GetAsync("/api/v3/openOrders", true, cancellationToken);
        bool valid = true;
        foreach (JsonElement order in openOrders.RootElement.EnumerateArray())
        {
            string symbol = order.GetProperty("symbol").GetString()!;
            InstrumentRuntime? runtime = null;
            foreach (InstrumentRuntime candidate in runtimes)
                if (candidate.Instrument.Symbol == symbol) { runtime = candidate; break; }
            if (runtime is null) continue;
            WorkingOrders working = runtime.WorkingOrders;
            bool buy = order.GetProperty("side").GetString() switch
            {
                "BUY" => true,
                "SELL" => false,
                _ => throw new InvalidOperationException($"Unknown open-order side for {symbol}.")
            };
            WorkingOrder slot = buy ? working.BidOrder : working.AskOrder;
            if (slot.IsActive) { valid = false; break; }
            OrderStatus status = order.GetProperty("status").GetString() switch
            {
                "NEW" => OrderStatus.New,
                "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
                "PENDING_CANCEL" => OrderStatus.PendingCancel,
                _ => throw new InvalidOperationException($"Unknown open-order status for {symbol}.")
            };
            slot = new WorkingOrder
            {
                OrderId = order.GetProperty("orderId").GetInt64(),
                PriceTicks = runtime.Instrument.ToPriceTicks(decimal.Parse(order.GetProperty("price").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture)),
                Quantity = runtime.Instrument.ToQuantityUnits(decimal.Parse(order.GetProperty("origQty").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture)),
                FilledQuantity = runtime.Instrument.ToQuantityUnits(decimal.Parse(order.GetProperty("executedQty").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture)),
                Status = status,
                IsActive = true
            };
            if (slot.OrderId <= 0 || slot.PriceTicks <= 0 || slot.Quantity <= 0 ||
                slot.FilledQuantity < 0 || slot.FilledQuantity > slot.Quantity)
                throw new InvalidOperationException($"Invalid open-order snapshot for {symbol}.");
            if (buy) working.BidOrder = slot; else working.AskOrder = slot;
            runtime.SetWorkingOrders(working);
        }
        if (!valid)
        {
            ledger.Invalidate();
            throw new InvalidOperationException("More than one open order exists on one side of a configured symbol.");
        }
        ledger.CompleteSnapshot();
    }

    public async Task LoadBooksAsync(InstrumentRuntime[] runtimes, CancellationToken cancellationToken)
    {
        foreach (InstrumentRuntime runtime in runtimes)
        {
            string path = $"/api/v3/depth?symbol={Uri.EscapeDataString(runtime.Instrument.Symbol)}&limit=100";
            using JsonDocument document = await GetAsync(path, false, cancellationToken);
            JsonElement root = document.RootElement;
            var bids = new BookLevel[root.GetProperty("bids").GetArrayLength()];
            var asks = new BookLevel[root.GetProperty("asks").GetArrayLength()];
            int i = 0;
            foreach (JsonElement level in root.GetProperty("bids").EnumerateArray())
                bids[i++] = ParseLevel(runtime.Instrument, level);
            i = 0;
            foreach (JsonElement level in root.GetProperty("asks").EnumerateArray())
                asks[i++] = ParseLevel(runtime.Instrument, level);
            if (!runtime.LoadSnapshot(bids, asks, root.GetProperty("lastUpdateId").GetInt64()))
            {
                runtime.Invalidate();
                throw new IOException($"Depth snapshot is incomplete for {runtime.Instrument.Symbol}.");
            }
        }
    }

    private static BookLevel ParseLevel(Instrument instrument, JsonElement level) => new()
    {
        PriceTicks = instrument.ToPriceTicks(decimal.Parse(level[0].GetString()!, System.Globalization.CultureInfo.InvariantCulture)),
        Quantity = instrument.ToQuantityUnits(decimal.Parse(level[1].GetString()!, System.Globalization.CultureInfo.InvariantCulture))
    };

    private async Task<JsonDocument> GetAsync(string path, bool signed, CancellationToken cancellationToken)
    {
        if (signed)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string query = $"timestamp={timestamp}";
            string signature = await SignAsync(query, cancellationToken);
            path += (path.Contains('?') ? "&" : "?") + query + "&signature=" + Uri.EscapeDataString(signature);
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (signed) request.Headers.Add("X-MBX-APIKEY", apiKey);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public async Task<string> SignAsync(string payload, CancellationToken cancellationToken)
    {
        string payloadPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(payloadPath, payload, Encoding.ASCII, cancellationToken);
            var start = new ProcessStartInfo("openssl")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("pkeyutl");
            start.ArgumentList.Add("-sign");
            start.ArgumentList.Add("-rawin");
            start.ArgumentList.Add("-inkey");
            start.ArgumentList.Add(privateKeyPath);
            start.ArgumentList.Add("-in");
            start.ArgumentList.Add(payloadPath);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start openssl.");
            using var output = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Ed25519 signing failed: " +
                    await process.StandardError.ReadToEndAsync(cancellationToken));
            return Convert.ToBase64String(output.GetBuffer(), 0, checked((int)output.Length));
        }
        finally
        {
            File.Delete(payloadPath);
        }
    }

    public void Dispose() => http.Dispose();
}
