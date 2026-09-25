using System.Diagnostics;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.Entities;
using AssumptionZero.Domain.InMemory;
using Org.SbeTool.Sbe.Dll;
using Spot_stream;

namespace AssumptionZero.Infrastructure.External;

public readonly record struct BestQuote(
    string Symbol,
    decimal BidPrice,
    decimal BidQuantity,
    decimal AskPrice,
    decimal AskQuantity,
    long UpdateId,
    long EventTimeMicros);

public readonly record struct DepthLevel(
    decimal Price,
    decimal Quantity);

public sealed class BinanceFeedReader
{
    private readonly string[] symbols;
    private readonly string apiKey;
    private readonly Uri uri;
    private readonly bool replay;
    private readonly bool syntheticReplay;
    private readonly bool logEvents;

    private readonly MessageHeader header = new();
    private readonly DepthDiffStreamEvent depthDecoder = new();
    private readonly BestBidAskStreamEvent quoteDecoder = new();
    private readonly TradesStreamEvent tradesDecoder = new();
    private readonly byte[] receiveBuffer = new byte[1024 * 1024];
    private readonly DirectBuffer directBuffer;
    private readonly InstrumentRuntime[] runtimes;
    private readonly byte[][] runtimeSymbols;
    private readonly BinanceAccountStream? accountStream;
    private readonly DepthLevel[] bidChanges = new DepthLevel[5_000];
    private readonly DepthLevel[] askChanges = new DepthLevel[5_000];
    private readonly decimal[] tradeQuantities = new decimal[8_192];
    private readonly bool[] tradeAggressors = new bool[8_192];

    private readonly Guid recordingId = Guid.NewGuid();
    private long recordingStartTimestamp;
    private long recordingSequence;

    public readonly Channel<RecordedMessage> recordedMessages =
        Channel.CreateBounded<RecordedMessage>(
            new BoundedChannelOptions(8192)
            {
                SingleReader = true,
                SingleWriter = true
            });

    public event Action<BestQuote>? QuoteReceived;
    public event Action<InstrumentRuntime, QuoteDecision>? DecisionProduced;

    /// <summary>Runs the same decoder used by the WebSocket receive loop; useful for deterministic replay checks.</summary>
    public void ProcessBinaryFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length > receiveBuffer.Length) throw new ArgumentOutOfRangeException(nameof(frame));
        frame.CopyTo(receiveBuffer);
        accountStream?.Drain();
        DecodeSbeMessage(frame.Length);
    }

    public BinanceFeedReader(string apiKey, string[] symbols, string baseUrl,
        bool replay = false, bool? logEvents = null, InstrumentRuntime[]? runtimes = null,
        BinanceAccountStream? accountStream = null, bool syntheticReplay = false)
    {
        if (!replay) ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (syntheticReplay && !replay) throw new ArgumentException("Synthetic books are for replay only.");
        this.apiKey = apiKey;
        this.symbols = symbols;
        this.replay = replay;
        this.syntheticReplay = syntheticReplay;
        this.logEvents = logEvents ?? !replay;
        this.runtimes = runtimes ?? [];
        this.accountStream = accountStream;
        runtimeSymbols = new byte[this.runtimes.Length][];
        for (int i = 0; i < runtimeSymbols.Length; i++)
            runtimeSymbols[i] = Encoding.UTF8.GetBytes(this.runtimes[i].Instrument.Symbol);
        directBuffer = new DirectBuffer(receiveBuffer);
        string streams = FormatStreams(symbols);
        uri = new Uri(replay ? baseUrl : $"{baseUrl}{streams}");
    }

    public async Task Start(
        CancellationToken cancellationToken = default,
        ExecutionMode executionMode = ExecutionMode.SeedData,
        string connectionString = "",
        Func<CancellationToken, Task>? afterConnect = null)
    {
        if (replay != (executionMode == ExecutionMode.Replay))
            throw new ArgumentException("Replay connection and execution mode must match.");
        Task? writerTask = null;

        if (executionMode == ExecutionMode.SeedData)
        {
            // Starts the background database writer.
            writerTask = this.PersistRecordedMessagesAsync(connectionString);
        }

        try
        {
            using var ws = PrepareClient();

            var connectedClient = await ws.ThenConnect(
                symbols ?? [],
                uri,
                cancellationToken);
            if (afterConnect is not null) await afterConnect(cancellationToken);
            recordingStartTimestamp = Stopwatch.GetTimestamp();
            await connectedClient.AndStartReceiving(
                message => ProcessingPipeline(message, executionMode),
                receiveBuffer,
                cancellationToken,
                allowNormalClosure: replay,
                logEvents: logEvents);
        }
        finally
        {
            if (writerTask is not null)
            {
                recordedMessages.Writer.TryComplete();
                await writerTask;
            }
        }
    }

    private void ProcessingPipeline(WsMessage message, ExecutionMode mode)
    {
        accountStream?.Drain();
        if (accountStream?.Faulted == true)
            throw new IOException("User-data stream lost account reconciliation.");
        if (mode == ExecutionMode.SeedData)
        {
            long now = Stopwatch.GetTimestamp();

            var recorded = new RecordedMessage(
                recordingId,
                ++recordingSequence,
                NanosecondTime.FromTicks(now - recordingStartTimestamp, Stopwatch.Frequency),
                (short)message.Type,
                message.Payload.AsSpan(0, message.Length).ToArray());

            if (!recordedMessages.Writer.TryWrite(recorded))
                throw new InvalidOperationException(
                    "Recording queue is full. Stop the capture to avoid losing messages.");

            return;
        }

        DecodeSbeMessage(message.Length);
    }

    private ClientWebSocket PrepareClient()
    {
        var ws = new ClientWebSocket();
        if (!replay) ws.Options.SetRequestHeader(
            BinanceConstants.BinanceApiKeyHeaderName,
            apiKey
        );
        ws.Options.KeepAliveInterval = TimeSpan.Zero;
        return ws;
    }

    private void DecodeSbeMessage(int length)
    {
        if (length < MessageHeader.Size)
        {
            if (!replay) throw new IOException($"Invalid SBE message of {length} bytes.");
            return;
        }

        if (!ValidFrame(receiveBuffer.AsSpan(0, length)))
        {
            if (!replay) throw new IOException("Invalid SBE frame; reconnect and reload snapshots.");
            return;
        }
        DirectBuffer buffer = directBuffer;
        header.Wrap(
            buffer,
            0,
            MessageHeader.SbeSchemaVersion);
        if (header.TemplateId == BestBidAskStreamEvent.TemplateId)
        {
            DecodeBestBidAsk(buffer);
            return;
        }

        if (header.TemplateId == DepthDiffStreamEvent.TemplateId)
        {
            DecodeDepthDiff(buffer);
            return;
        }

        if (header.TemplateId == TradesStreamEvent.TemplateId)
        {
            DecodeTrades(buffer);
            return;
        }

        if (!replay) throw new IOException($"Unknown SBE template {header.TemplateId}.");
    }

    private void DecodeBestBidAsk(DirectBuffer buffer)
    {
        if (QuoteReceived is null && !logEvents && !syntheticReplay) return;
        quoteDecoder.WrapForDecode(
            buffer,
            MessageHeader.Size,
            header.BlockLength,
            header.Version);
        sbyte priceExponent = quoteDecoder.PriceExponent;
        sbyte qtyExponent = quoteDecoder.QtyExponent;
        decimal bidPrice = DecodeDecimal(
            quoteDecoder.BidPrice,
            priceExponent);
        decimal bidQuantity = DecodeDecimal(
            quoteDecoder.BidQty,
            qtyExponent);
        decimal askPrice = DecodeDecimal(
            quoteDecoder.AskPrice,
            priceExponent);
        decimal askQuantity = DecodeDecimal(
            quoteDecoder.AskQty,
            qtyExponent);
        if (quoteDecoder.SymbolLength() > 64) return;
        Span<byte> symbolBytes = stackalloc byte[64];
        int symbolLength = quoteDecoder.GetSymbol(symbolBytes);
        if (syntheticReplay)
        {
            InstrumentRuntime? runtime = FindRuntime(symbolBytes[..symbolLength]);
            if (runtime is not null && !runtime.OrderBook.IsSynchronized)
            {
                Instrument instrument = runtime.Instrument;
                long bidTicks = instrument.ToPriceTicks(bidPrice);
                long askTicks = instrument.ToPriceTicks(askPrice);
                long bidUnits = instrument.ToQuantityUnits(bidQuantity);
                long askUnits = instrument.ToQuantityUnits(askQuantity);
                if (instrument.Price(bidTicks) == bidPrice && instrument.Price(askTicks) == askPrice &&
                    instrument.Quantity(bidUnits) == bidQuantity && instrument.Quantity(askUnits) == askQuantity)
                    runtime.BootstrapSyntheticBook(bidTicks, bidUnits, askTicks, askUnits,
                        quoteDecoder.BookUpdateId);
            }
        }
        if (QuoteReceived is null && !logEvents) return;
        string symbol = Encoding.UTF8.GetString(symbolBytes[..symbolLength]);
        var quote = new BestQuote(
            symbol,
            bidPrice,
            bidQuantity,
            askPrice,
            askQuantity,
            quoteDecoder.BookUpdateId,
            quoteDecoder.EventTime);
        QuoteReceived?.Invoke(quote);
        if (logEvents) Console.WriteLine($"QUOTE {symbol} bid={bidPrice} ({bidQuantity}) ask={askPrice} ({askQuantity}) update={quote.UpdateId}");
    }

    private void DecodeDepthDiff(DirectBuffer buffer)
    {
        depthDecoder.WrapForDecode(
            buffer,
            MessageHeader.Size,
            header.BlockLength,
            header.Version);
        sbyte priceExponent = depthDecoder.PriceExponent;
        sbyte qtyExponent = depthDecoder.QtyExponent;
        int bidCount = 0, askCount = 0;
        var bids = depthDecoder.Bids;
        while (bids.HasNext)
        {
            var bid = bids.Next();
            if (bidCount == bidChanges.Length) return;
            bidChanges[bidCount++] = new DepthLevel(
                DecodeDecimal(bid.Price, priceExponent),
                DecodeDecimal(bid.Qty, qtyExponent));
        }

        var asks = depthDecoder.Asks;

        while (asks.HasNext)
        {
            var ask = asks.Next();
            if (askCount == askChanges.Length) return;
            askChanges[askCount++] = new DepthLevel(
                DecodeDecimal(ask.Price, priceExponent),
                DecodeDecimal(ask.Qty, qtyExponent));
        }

        if (depthDecoder.SymbolLength() > 64) return;
        Span<byte> symbolBytes = stackalloc byte[64];
        int symbolLength = depthDecoder.GetSymbol(symbolBytes);
        InstrumentRuntime? runtime = FindRuntime(symbolBytes[..symbolLength]);
        if (syntheticReplay && runtime is not null && !runtime.OrderBook.IsSynchronized)
        {
            for (int i = 0; i < bidCount && !runtime.OrderBook.IsSynchronized; i++)
            {
                DepthLevel level = bidChanges[i];
                if (level.Price > 0 && level.Quantity > 0)
                    runtime.ObserveSyntheticDepth(true, runtime.Instrument.ToPriceTicks(level.Price),
                        runtime.Instrument.ToQuantityUnits(level.Quantity), depthDecoder.LastBookUpdateId);
            }
            for (int i = 0; i < askCount && !runtime.OrderBook.IsSynchronized; i++)
            {
                DepthLevel level = askChanges[i];
                if (level.Price > 0 && level.Quantity > 0)
                    runtime.ObserveSyntheticDepth(false, runtime.Instrument.ToPriceTicks(level.Price),
                        runtime.Instrument.ToQuantityUnits(level.Quantity), depthDecoder.LastBookUpdateId);
            }
        }
        bool wasSynchronized = runtime?.OrderBook.IsSynchronized ?? false;
        if (runtime is not null && runtime.BeginDepth(depthDecoder.FirstBookUpdateId, depthDecoder.LastBookUpdateId))
        {
            bool valid = true;
            for (int i = 0; i < bidCount && valid; i++)
            {
                DepthLevel level = bidChanges[i];
                long price = runtime.Instrument.ToPriceTicks(level.Price);
                long quantity = runtime.Instrument.ToQuantityUnits(level.Quantity);
                valid = runtime.Instrument.Price(price) == level.Price &&
                        runtime.Instrument.Quantity(quantity) == level.Quantity &&
                        runtime.ApplyBid(price, quantity);
            }
            for (int i = 0; i < askCount && valid; i++)
            {
                DepthLevel level = askChanges[i];
                long price = runtime.Instrument.ToPriceTicks(level.Price);
                long quantity = runtime.Instrument.ToQuantityUnits(level.Quantity);
                valid = runtime.Instrument.Price(price) == level.Price &&
                        runtime.Instrument.Quantity(quantity) == level.Quantity &&
                        runtime.ApplyAsk(price, quantity);
            }
            if (!valid) runtime.Invalidate();
            QuoteDecision decision = valid ? runtime.EndDepth(checked(depthDecoder.EventTime * 1_000)) : runtime.Evaluate();
            DecisionProduced?.Invoke(runtime, decision);
            if (runtime.SignalMemory.Faulted && !replay)
                throw new IOException($"Signal history exhausted for {runtime.Instrument.Symbol}; reconnect and reload snapshot.");
            if (!runtime.OrderBook.IsSynchronized && !replay)
                throw new IOException($"Depth coverage lost for {runtime.Instrument.Symbol}; reconnect and reload snapshot.");
        }
        else if (runtime is not null && wasSynchronized && !runtime.OrderBook.IsSynchronized)
        {
            DecisionProduced?.Invoke(runtime, runtime.Evaluate());
            if (!replay)
                throw new IOException($"Depth sequence gap for {runtime.Instrument.Symbol}; reconnect and reload snapshot.");
        }
        if (!logEvents) return;
        string symbol = Encoding.UTF8.GetString(symbolBytes[..symbolLength]);
        Console.WriteLine(
            $"DEPTH {symbol} " +
            $"{depthDecoder.FirstBookUpdateId}" +
            $"->{depthDecoder.LastBookUpdateId} " +
            $"bids={bidCount} " +
            $"asks={askCount}");
        for (int i = 0; i < bidCount; i++)
        {
            DepthLevel bid = bidChanges[i];
            Console.WriteLine(
                $"  BID CHANGE price={bid.Price} qty={bid.Quantity}");
        }

        for (int i = 0; i < askCount; i++)
        {
            DepthLevel ask = askChanges[i];
            Console.WriteLine(
                $"  ASK CHANGE price={ask.Price} qty={ask.Quantity}");
        }
    }

    private void DecodeTrades(DirectBuffer buffer)
    {
        tradesDecoder.WrapForDecode(buffer, MessageHeader.Size, header.BlockLength, header.Version);
        int count = 0;
        var group = tradesDecoder.Trades;
        while (group.HasNext)
        {
            var trade = group.Next();
            if (count == tradeQuantities.Length) return;
            tradeQuantities[count] = DecodeDecimal(trade.Qty, tradesDecoder.QtyExponent);
            tradeAggressors[count++] = trade.IsBuyerMaker == BoolEnum.False;
        }
        if (tradesDecoder.SymbolLength() > 64) return;
        Span<byte> symbol = stackalloc byte[64];
        int length = tradesDecoder.GetSymbol(symbol);
        InstrumentRuntime? runtime = FindRuntime(symbol[..length]);
        if (runtime is null) return;
        long timeNs = checked(tradesDecoder.EventTime * 1_000);
        for (int i = 0; i < count; i++)
        {
            double quantity = (double)tradeQuantities[i];
            if (quantity <= 0 || !double.IsFinite(quantity))
            {
                runtime.Invalidate();
                return;
            }
            QuoteDecision decision = runtime.OnMarketTrade(timeNs, quantity, tradeAggressors[i]);
            DecisionProduced?.Invoke(runtime, decision);
            if (runtime.SignalMemory.Faulted && !replay)
                throw new IOException($"Trade history exhausted for {runtime.Instrument.Symbol}; reconnect and reload snapshot.");
        }
    }

    private InstrumentRuntime? FindRuntime(ReadOnlySpan<byte> symbol)
    {
        for (int i = 0; i < runtimeSymbols.Length; i++)
            if (symbol.SequenceEqual(runtimeSymbols[i])) return runtimes[i];
        return null;
    }

    private static bool ValidFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 8) return false;
        int block = BinaryPrimitives.ReadUInt16LittleEndian(frame);
        int template = BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]);
        int schema = BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]);
        int version = BinaryPrimitives.ReadUInt16LittleEndian(frame[6..]);
        if (schema != 1 || version != 0 || frame.Length < 8 + block + 1) return false;
        int offset = 8 + block;
        if (template == BestBidAskStreamEvent.TemplateId)
        {
            if (block < BestBidAskStreamEvent.BlockLength) return false;
        }
        else if (template == DepthDiffStreamEvent.TemplateId)
        {
            if (block < DepthDiffStreamEvent.BlockLength) return false;
            for (int i = 0; i < 2; i++)
            {
                if (frame.Length - offset < 4) return false;
                int groupBlock = BinaryPrimitives.ReadUInt16LittleEndian(frame[offset..]);
                int count = BinaryPrimitives.ReadUInt16LittleEndian(frame[(offset + 2)..]);
                if (groupBlock < 16 || count > 5_000) return false;
                offset += 4;
                if ((long)groupBlock * count > frame.Length - offset) return false;
                offset += groupBlock * count;
            }
        }
        else if (template == TradesStreamEvent.TemplateId)
        {
            if (block < TradesStreamEvent.BlockLength || frame.Length - offset < 6) return false;
            int groupBlock = BinaryPrimitives.ReadUInt16LittleEndian(frame[offset..]);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(frame[(offset + 2)..]);
            if (groupBlock < 25 || count > 8_192) return false;
            offset += 6;
            if ((long)groupBlock * count > frame.Length - offset) return false;
            offset += groupBlock * (int)count;
        }
        else return false;
        return offset < frame.Length && offset + 1 + frame[offset] == frame.Length;
    }

    private static string FormatStreams(string[]? symbolsToFormat) => string.Join(
        "/",
        symbolsToFormat?.SelectMany(symbol =>
        {
            string name = symbol.ToLowerInvariant();

            return new[]
            {
                $"{name}@depth",
                $"{name}@bestBidAsk",
                $"{name}@trade"
            };
        }) ?? []);

    private static decimal DecodeDecimal(
        long mantissa,
        sbyte exponent)
    {
        decimal value = mantissa;
        if (exponent > 0)
        {
            for (int i = 0; i < exponent; i++)
                value *= 10m;
        }
        else
        {
            for (int i = 0; i < -exponent; i++)
                value /= 10m;
        }

        return value;
    }

    public readonly record struct WsMessage(
        WebSocketMessageType Type,
        byte[] Payload,
        int Length);

    public readonly record struct RecordedMessage(
        Guid RecordingId,
        long SequenceNumber,
        long ElapsedNanoseconds,
        short MessageType,
        byte[] Payload);
}
