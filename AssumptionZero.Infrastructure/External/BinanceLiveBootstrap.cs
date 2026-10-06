using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.Entities;
using AssumptionZero.Domain.InMemory;

namespace AssumptionZero.Infrastructure.External;

/// <summary>Read-only Spot Testnet startup data. All HTTP and signing work is outside the event path.</summary>
public sealed class BinanceLiveBootstrap(string apiKey, string privateKeyPath) : IDisposable
{
    private readonly HttpClient httpClient = new() { BaseAddress = new Uri(AppConstants.BinanceTestnet) };

    public async Task<Instrument[]> LoadInstruments(string[] wanted, CancellationToken cancellationToken)
    {
        using JsonDocument instrumentsData = await Get(
            "/api/v3/exchangeInfo",
            false, cancellationToken
        );

        var instruments = new List<Instrument>(wanted.Length);
        foreach (JsonElement symbol in instrumentsData.RootElement.GetProperty("symbols").EnumerateArray())
        {
            string name = symbol.GetProperty("symbol").GetString()!;
            if (!wanted.Contains(name, StringComparer.Ordinal)) continue;
            if (symbol.GetProperty("status").GetString() != "TRADING") continue;
            Instrument? instrument = symbol
                .ExtractFields()
                .ThenBuild(instruments.Count);

            if (instrument is not null) instruments.Add(instrument);
        }

        if (instruments.Count == 0)
            throw new InvalidOperationException("None of the configured symbols are available on Spot Testnet.");
        return instruments.ToArray();
    }

    public async Task LoadAccount(AccountRiskLedger ledger, InstrumentRuntime[] runtimes,
        CancellationToken cancellationToken, bool initializeInventory = false)
    {
        using JsonDocument accountData = await Get("/api/v3/account", true, cancellationToken);
        BeginAccountReconciliation(accountData.RootElement, ledger, runtimes);
        ApplyBalances(accountData.RootElement.GetProperty("balances"), ledger);
        RefreshInventories(runtimes, initializeInventory);

        using JsonDocument openOrders = await Get("/api/v3/openOrders", true, cancellationToken);
        RestoreOpenOrders(openOrders.RootElement, ledger, runtimes);

        ledger.CompleteSnapshot();
    }
    
    public async Task LoadBooks(InstrumentRuntime[] runtimes, CancellationToken cancellationToken)
    {
        foreach (InstrumentRuntime runtime in runtimes)
        {
            string path = $"/api/v3/depth?symbol={Uri.EscapeDataString(runtime.Instrument.Symbol)}&limit=100";
            using JsonDocument data = await Get(path, false, cancellationToken);
            JsonElement root = data.RootElement;
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


    private static void BeginAccountReconciliation(JsonElement account, AccountRiskLedger ledger,
        InstrumentRuntime[] runtimes)
    {
        decimal makerFee = ReadDecimal(account.GetProperty("commissionRates"), "maker");
        ledger.BeginSnapshot(makerFee);
        foreach (InstrumentRuntime runtime in runtimes) runtime.BeginAccountReconciliation();
    }

    private static void ApplyBalances(JsonElement balances, AccountRiskLedger ledger)
    {
        foreach (JsonElement balance in balances.EnumerateArray())
        {
            int assetId = ledger.FindAsset(balance.GetProperty("asset").GetString()!);
            if (assetId < 0) continue;

            ledger.SetBalance(assetId, ReadDecimal(balance, "free"), ReadDecimal(balance, "locked"));
        }
    }

    private static void RefreshInventories(InstrumentRuntime[] runtimes, bool initializeInventory)
    {
        foreach (InstrumentRuntime runtime in runtimes)
        {
            runtime.OnBalanceChanged();
            if (!initializeInventory) continue;

            long current = runtime.Inventory.CurrentQuantity;
            runtime.ConfigureInventory(current, checked(current + runtime.Config.BaseQuantity * 10));
        }
    }

    private static void RestoreOpenOrders(JsonElement openOrders, AccountRiskLedger ledger,
        InstrumentRuntime[] runtimes)
    {
        foreach (JsonElement order in openOrders.EnumerateArray())
        {
            string symbol = order.GetProperty("symbol").GetString()!;
            InstrumentRuntime? runtime = Array.Find(runtimes, candidate => candidate.Instrument.Symbol == symbol);
            if (runtime is null) continue;

            bool buy = order.GetProperty("side").GetString() switch
            {
                "BUY" => true,
                "SELL" => false,
                _ => throw new InvalidOperationException($"Unknown open-order side for {symbol}.")
            };
            WorkingOrders working = runtime.WorkingOrders;
            WorkingOrder existing = buy ? working.BidOrder : working.AskOrder;
            if (existing.IsActive)
            {
                ledger.Invalidate();
                throw new InvalidOperationException("More than one open order exists on one side of a configured symbol.");
            }

            WorkingOrder restored = ParseOpenOrder(order, runtime.Instrument);
            if (buy) working.BidOrder = restored;
            else working.AskOrder = restored;
            runtime.SetWorkingOrders(working);
        }
    }

    private static WorkingOrder ParseOpenOrder(JsonElement order, Instrument instrument)
    {
        OrderStatus status = order.GetProperty("status").GetString() switch
        {
            "NEW" => OrderStatus.New,
            "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
            "PENDING_CANCEL" => OrderStatus.PendingCancel,
            _ => throw new InvalidOperationException($"Unknown open-order status for {instrument.Symbol}.")
        };
        var restored = new WorkingOrder
        {
            OrderId = order.GetProperty("orderId").GetInt64(),
            PriceTicks = instrument.ToPriceTicks(ReadDecimal(order, "price")),
            Quantity = instrument.ToQuantityUnits(ReadDecimal(order, "origQty")),
            FilledQuantity = instrument.ToQuantityUnits(ReadDecimal(order, "executedQty")),
            Status = status,
            IsActive = true
        };
        if (restored.OrderId <= 0 || restored.PriceTicks <= 0 || restored.Quantity <= 0 ||
            restored.FilledQuantity < 0 || restored.FilledQuantity > restored.Quantity)
            throw new InvalidOperationException($"Invalid open-order snapshot for {instrument.Symbol}.");

        return restored;
    }

    private static decimal ReadDecimal(JsonElement element, string propertyName) =>
        decimal.Parse(element.GetProperty(propertyName).GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
    
    private static BookLevel ParseLevel(Instrument instrument, JsonElement level) => new()
    {
        PriceTicks =
            instrument.ToPriceTicks(decimal.Parse(level[0].GetString()!,
                System.Globalization.CultureInfo.InvariantCulture)),
        Quantity = instrument.ToQuantityUnits(decimal.Parse(level[1].GetString()!,
            System.Globalization.CultureInfo.InvariantCulture))
    };

    private async Task<JsonDocument> Get(string path, bool signed, CancellationToken cancellationToken)
    {
        if (signed)
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string query = $"timestamp={timestamp}";
            string signature = await Sign(query, cancellationToken);
            path += (path.Contains('?') ? "&" : "?") + query + "&signature=" + Uri.EscapeDataString(signature);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (signed) request.Headers.Add("X-MBX-APIKEY", apiKey);
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    public async Task<string> Sign(string payload, CancellationToken cancellationToken)
    {
        string payloadPath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(payloadPath, payload, Encoding.ASCII, cancellationToken);
            var signature = await SignWithOpenSsl(payloadPath, cancellationToken);
            return signature;
        }
        finally
        {
            File.Delete(payloadPath);
        }
    }

    private async Task<string> SignWithOpenSsl(
        string payloadPath,
        CancellationToken cancellationToken)
    {
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
        using Process process =
            Process.Start(start) ?? throw new InvalidOperationException("Could not start openssl.");
        using var output = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Ed25519 signing failed: " +
                                                await process.StandardError.ReadToEndAsync(cancellationToken));
        return Convert.ToBase64String(output.GetBuffer(), 0, checked((int)output.Length));
    }

    public void Dispose()
        => httpClient.Dispose();
}