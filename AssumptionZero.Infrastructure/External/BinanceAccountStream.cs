using System.Buffers.Text;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.InMemory;

namespace AssumptionZero.Infrastructure.External;

public enum AccountEventKind : byte
{
    Balance,
    Execution,
    Invalidate
}

public readonly record struct AccountEvent(
    AccountEventKind Kind,
    int Asset,
    decimal Free,
    decimal Locked,
    int Runtime,
    bool Buy,
    long OrderId,
    long Filled,
    long FillPriceTicks,
    long TimeNs,
    OrderStatus Status);

/// <summary>Read-only user-data stream. One socket producer publishes value events to the market writer.</summary>
public sealed class BinanceAccountStream(
    string apiKey,
    BinanceLiveBootstrap bootstrap,
    AccountRiskLedger ledger,
    InstrumentRuntime[] runtimes
)
    : IAsyncDisposable
{
    private readonly SpscRingBuffer<AccountEvent> events = new(8_192);
    private readonly byte[] buffer = new byte[1024 * 1024];
    private readonly ClientWebSocket socket = new();
    private Task? runTask;
    private int faulted;
    public bool Faulted => Volatile.Read(ref faulted) != 0;

    public async Task Start(CancellationToken cancellationToken)
    {
        await socket.ConnectAsync(new Uri(AppConstants.WsBinanceV3), cancellationToken);
        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string signature = await bootstrap.Sign($"apiKey={apiKey}&timestamp={timestamp}", cancellationToken);
        string request = JsonSerializer.Serialize(new
        {
            id = 1,
            method = "userDataStream.subscribe.signature",
            @params = new { apiKey = apiKey, timestamp, signature }
        });
        byte[] bytes = Encoding.UTF8.GetBytes(request);
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken);
        int length = await Receive(cancellationToken);
        using (JsonDocument response = JsonDocument.Parse(buffer.AsMemory(0, length)))
        {
            if (!response.RootElement.TryGetProperty("status", out JsonElement status) || status.GetInt32() != 200)
                throw new InvalidOperationException("Binance user-data subscription failed.");
        }

        runTask = Run(cancellationToken);
    }

    private async Task Run(CancellationToken cancellationToken)
    {
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                int length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (length == buffer.Length)
                        throw new InvalidDataException("Account event exceeded receive buffer.");
                    result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        throw new IOException("Account stream closed.");
                    length += result.Count;
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text)
                    throw new InvalidDataException("Unexpected account event format.");
                if (!ParseEvent(buffer.AsSpan(0, length))) Volatile.Write(ref faulted, 1);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            Volatile.Write(ref faulted, 1);
        }
    }

    private async Task<int> Receive(CancellationToken cancellationToken)
    {
        int length = 0;
        ValueWebSocketReceiveResult result;
        do
        {
            if (length == buffer.Length) throw new InvalidDataException("Account event exceeded receive buffer.");
            result = await socket.ReceiveAsync(buffer.AsMemory(length), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Account stream closed.");
            length += result.Count;
        } while (!result.EndOfMessage);

        if (result.MessageType != WebSocketMessageType.Text)
            throw new InvalidDataException("Unexpected account event format.");
        return length;
    }

    private bool ParseEvent(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("event"u8)) continue;
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            return ParseEventObject(ref reader);
        }

        return true; // Subscription or control response.
    }

    private bool ParseEventObject(ref Utf8JsonReader reader)
    {
        AccountEventKind kind = AccountEventKind.Invalidate;
        int runtimeIndex = -1;
        bool buy = false;
        long orderId = 0, timeMs = 0;
        decimal filled = 0, fillPrice = 0;
        OrderStatus? status = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) break;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            if (reader.ValueTextEquals("B"u8))
            {
                if (!reader.Read()) return false;
                if (reader.TokenType == JsonTokenType.StartArray && !ParseBalances(ref reader)) return false;
                continue;
            }

            int field = reader.ValueSpan.Length == 1 ? reader.ValueSpan[0] : 0;
            if (!reader.Read()) return false;
            switch (field)
            {
                case 'e':
                    if (reader.ValueTextEquals("outboundAccountPosition"u8)) kind = AccountEventKind.Balance;
                    else if (reader.ValueTextEquals("executionReport"u8)) kind = AccountEventKind.Execution;
                    else return false;
                    break;
                case 's': runtimeIndex = FindRuntime(reader.ValueSpan); break;
                case 'S': buy = reader.ValueTextEquals("BUY"u8); break;
                case 'i': orderId = reader.GetInt64(); break;
                case 'z':
                    if (!ParseDecimal(reader.ValueSpan, out filled)) return false;
                    break;
                case 'L':
                    if (!ParseDecimal(reader.ValueSpan, out fillPrice)) return false;
                    break;
                case 'T': timeMs = reader.GetInt64(); break;
                case 'X': status = ParseStatus(reader.ValueSpan); break;
            }
        }

        if (kind == AccountEventKind.Balance) return true;
        if (kind != AccountEventKind.Execution || runtimeIndex < 0 || orderId <= 0 || status is null) return false;
        InstrumentRuntime runtime = runtimes[runtimeIndex];
        long quantity = runtime.Instrument.ToQuantityUnits(filled);
        long price = runtime.Instrument.ToPriceTicks(fillPrice);
        return events.TryEnqueue(new AccountEvent(AccountEventKind.Execution, -1, 0, 0,
            runtimeIndex, buy, orderId, quantity, price, checked(timeMs * 1_000_000), status.Value));
    }

    private bool ParseBalances(ref Utf8JsonReader reader)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return true;
            if (reader.TokenType != JsonTokenType.StartObject) return false;
            int asset = -1;
            decimal free = 0, locked = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                int field = reader.ValueSpan.Length == 1 ? reader.ValueSpan[0] : 0;
                if (!reader.Read()) return false;
                if (field == 'a') asset = ledger.FindAsset(reader.ValueSpan);
                else if (field == 'f' && !ParseDecimal(reader.ValueSpan, out free)) return false;
                else if (field == 'l' && !ParseDecimal(reader.ValueSpan, out locked)) return false;
            }

            if (asset >= 0 && !events.TryEnqueue(new AccountEvent(AccountEventKind.Balance, asset, free, locked,
                    -1, false, 0, 0, 0, 0, default))) return false;
        }

        return false;
    }

    private int FindRuntime(ReadOnlySpan<byte> symbol)
    {
        for (int i = 0; i < runtimes.Length; i++)
        {
            ReadOnlySpan<char> name = runtimes[i].Instrument.Symbol;
            if (name.Length != symbol.Length) continue;
            bool equal = true;
            for (int j = 0; j < name.Length; j++)
                if (name[j] != symbol[j])
                {
                    equal = false;
                    break;
                }

            if (equal) return i;
        }

        return -1;
    }

    private static bool ParseDecimal(ReadOnlySpan<byte> text, out decimal value) =>
        Utf8Parser.TryParse(text, out value, out int consumed) && consumed == text.Length;

    private static OrderStatus? ParseStatus(ReadOnlySpan<byte> value)
    {
        if (value.SequenceEqual("NEW"u8)) return OrderStatus.New;
        if (value.SequenceEqual("PARTIALLY_FILLED"u8)) return OrderStatus.PartiallyFilled;
        if (value.SequenceEqual("FILLED"u8)) return OrderStatus.Filled;
        if (value.SequenceEqual("CANCELED"u8)) return OrderStatus.Cancelled;
        if (value.SequenceEqual("REJECTED"u8)) return OrderStatus.Rejected;
        if (value.SequenceEqual("EXPIRED"u8)) return OrderStatus.Expired;
        if (value.SequenceEqual("PENDING_CANCEL"u8)) return OrderStatus.PendingCancel;
        return null;
    }

    public void Drain()
    {
        if (Volatile.Read(ref faulted) != 0)
        {
            ledger.Invalidate();
            foreach (InstrumentRuntime runtime in runtimes) runtime.Invalidate();
            return;
        }

        while (events.TryDequeue(out AccountEvent item))
        {
            if (item.Kind == AccountEventKind.Balance)
            {
                ledger.SetBalance(item.Asset, item.Free, item.Locked);
                foreach (InstrumentRuntime runtime in runtimes) runtime.OnBalanceChanged(item.Asset);
            }
            else if (item.Kind == AccountEventKind.Execution)
                runtimes[item.Runtime].OnExecution(item.Buy, item.OrderId, item.Filled,
                    item.FillPriceTicks, item.TimeNs, item.Status);
            else ledger.Invalidate();
        }
    }

    public async ValueTask DisposeAsync()
    {
        socket.Abort();
        if (runTask is not null) await runTask;
        socket.Dispose();
    }
}