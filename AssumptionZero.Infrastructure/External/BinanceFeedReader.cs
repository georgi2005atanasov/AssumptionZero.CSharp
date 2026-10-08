using AssumptionZero.Application.Interfaces.External;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.Entities;
using AssumptionZero.Domain.InMemory;
using AssumptionZero.Infrastructure.Benchmarking;
using Org.SbeTool.Sbe.Dll;
using Spot_stream;

namespace AssumptionZero.Infrastructure.External;

public sealed class BinanceFeedReader : IDataFeedReader
{
    private readonly string[] symbols;
    private readonly string apiKey;
    private readonly Uri uri;
    private readonly FeedMode mode;
    private bool IsReplay => mode is FeedMode.ReplayWithSnapshot or FeedMode.ReplayWithEstimatedBook;
    private bool EstimatesBook => mode == FeedMode.ReplayWithEstimatedBook;

    private readonly bool logEvents;

    private readonly MessageHeader header = new();

    private readonly DepthDiffStreamEvent depthDecoder = new();

    private readonly BestBidAskStreamEvent quoteDecoder = new();

    private readonly TradesStreamEvent tradesDecoder = new();
    private readonly byte[] receiveBuffer = new byte[1024 * 1024];
    private readonly DirectBuffer directBuffer;
    private readonly InstrumentRuntime[] runtimes;
    private readonly byte[][] runtimeSymbols;
    private readonly IAccountStream? accountStream;
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
    public ReplayBenchmarkRecorder? Benchmark { get; set; }

    /// <summary>
    /// Runs the same decoder used by the WebSocket receive loop; useful for deterministic replay checks.
    /// </summary>
    public void ProcessBinaryFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length > receiveBuffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }

        frame.CopyTo(receiveBuffer);
        accountStream?.Drain();
        DecodeSbeMessage(frame.Length);
    }

    public BinanceFeedReader(
        string apiKey,
        string[] symbols,
        string baseUrl,
        FeedMode mode,
        bool? logEvents = null,
        InstrumentRuntime[]? runtimes = null,
        IAccountStream? accountStream = null)
    {
        if (mode is FeedMode.Capture or FeedMode.Live)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        }

        this.apiKey = apiKey;
        this.symbols = symbols;
        this.mode = mode;
        this.logEvents = logEvents ?? !IsReplay;
        this.runtimes = runtimes ?? [];
        if (this.runtimes.Any(runtime => runtime.EstimatesBookFromFeed != EstimatesBook))
        {
            throw new ArgumentException("Feed mode and instrument book initialization must match.", nameof(runtimes));
        }

        this.accountStream = accountStream;
        runtimeSymbols = new byte[this.runtimes.Length][];
        for (int i = 0; i < runtimeSymbols.Length; i++)
        {
            runtimeSymbols[i] = Encoding.UTF8.GetBytes(this.runtimes[i].Instrument.Symbol);
        }

        directBuffer = new DirectBuffer(receiveBuffer);
        string streams = FormatStreams(symbols);
        uri = new Uri(IsReplay ? baseUrl : $"{baseUrl}{streams}");
    }

    public async Task Start(
        CancellationToken cancellationToken = default,
        string connectionString = "",
        Func<CancellationToken, Task>? afterConnect = null)
    {
        if (Benchmark is not null && !IsReplay)
        {
            throw new ArgumentException("Benchmark recording requires replay mode.");
        }

        Task? writerTask = null;
        if (mode == FeedMode.Capture)
        {
            // Starts the background database writer.
            writerTask = this.PersistRecordedMessages(connectionString);
        }

        try
        {
            using var ws = PrepareClient();
            var connectedClient = await ws.ThenConnect(symbols ?? [], uri, cancellationToken);
            if (afterConnect is not null)
            {
                await afterConnect(cancellationToken);
            }

            EnableBenchmarkStages();
            recordingStartTimestamp = Stopwatch.GetTimestamp();
            await connectedClient.AndStartReceiving(
                ProcessingPipeline,
                receiveBuffer,
                cancellationToken,
                allowNormalClosure: IsReplay,
                logEvents: logEvents,
                nonBinaryFrame: Benchmark is null ? null : RecordNonBinaryFrame);
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

    private void EnableBenchmarkStages()
    {
        if (Benchmark is null)
        {
            return;
        }

        foreach (InstrumentRuntime runtime in runtimes)
        {
            runtime.BenchmarkStages = true;
        }
    }

    private void RecordNonBinaryFrame(WsMessage message)
    {
        Benchmark!.BeginFrame(message.Type, message.Length, message.ReceivedTimestamp);
        Benchmark.EndFrame();
    }

    private void ProcessingPipeline(WsMessage message)
    {
        Benchmark?.BeginFrame(message.Type, message.Length, message.ReceivedTimestamp);
        try
        {
            accountStream?.Drain();
            if (accountStream?.Faulted == true)
            {
                throw new IOException("User-data stream lost account reconciliation.");
            }

            if (mode == FeedMode.Capture)
            {
                long now = Stopwatch.GetTimestamp();
                var recorded = new RecordedMessage(
                    recordingId,
                    ++recordingSequence,
                    NanosecondTime.FromTicks(now - recordingStartTimestamp, Stopwatch.Frequency),
                    (short)message.Type,
                    message.Payload.AsSpan(0, message.Length).ToArray());
                if (!recordedMessages.Writer.TryWrite(recorded))
                {
                    throw new InvalidOperationException(
                        "Recording queue is full. Stop the capture to avoid losing messages.");
                }

                return;
            }

            DecodeSbeMessage(message.Length);
        }
        finally
        {
            Benchmark?.EndFrame();
        }
    }

    private ClientWebSocket PrepareClient()
    {
        var ws = new ClientWebSocket();
        if (!IsReplay)
        {
            ws.Options.SetRequestHeader(BinanceConstants.BinanceApiKeyHeaderName, apiKey);
        }

        ws.Options.KeepAliveInterval = TimeSpan.Zero;
        return ws;
    }

    private void DecodeSbeMessage(int length)
    {
        if (!ValidateMessage(length))
        {
            return;
        }

        DirectBuffer buffer = directBuffer;
        header.Wrap(buffer, 0, MessageHeader.SbeSchemaVersion);
        Benchmark?.SetTemplate(header.TemplateId);
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

        if (!IsReplay)
        {
            throw new IOException($"Unknown SBE template {header.TemplateId}.");
        }
    }

    private bool ValidateMessage(int length)
    {
        if (length < MessageHeader.Size)
        {
            if (!IsReplay)
            {
                throw new IOException($"Invalid SBE message of {length} bytes.");
            }

            return false;
        }

        if (!ValidFrame(receiveBuffer.AsSpan(0, length)))
        {
            if (!IsReplay)
            {
                throw new IOException("Invalid SBE frame; reconnect and reload snapshots.");
            }

            return false;
        }

        return true;
    }

    private void DecodeBestBidAsk(DirectBuffer buffer)
    {
        long decodeStart = Benchmark is null ? 0 : Stopwatch.GetTimestamp();
        if (QuoteReceived is null && !logEvents && !EstimatesBook)
        {
            return;
        }

        quoteDecoder.WrapForDecode(buffer, MessageHeader.Size, header.BlockLength, header.Version);
        sbyte priceExponent = quoteDecoder.PriceExponent;
        sbyte qtyExponent = quoteDecoder.QtyExponent;
        decimal bidPrice = DecodeDecimal(quoteDecoder.BidPrice, priceExponent);
        decimal bidQuantity = DecodeDecimal(quoteDecoder.BidQty, qtyExponent);
        decimal askPrice = DecodeDecimal(quoteDecoder.AskPrice, priceExponent);
        decimal askQuantity = DecodeDecimal(quoteDecoder.AskQty, qtyExponent);
        if (quoteDecoder.SymbolLength() > 64)
        {
            return;
        }

        Span<byte> symbolBytes = stackalloc byte[64];
        int symbolLength = quoteDecoder.GetSymbol(symbolBytes);
        if (EstimatesBook)
        {
            SeedBookFromQuote(
                symbolBytes[..symbolLength],
                priceExponent,
                qtyExponent,
                bidPrice,
                bidQuantity,
                askPrice,
                askQuantity);
        }

        Benchmark?.AddDecode(Stopwatch.GetTimestamp() - decodeStart);
        if (QuoteReceived is null && !logEvents)
        {
            return;
        }

        PublishQuote(symbolBytes[..symbolLength], bidPrice, bidQuantity, askPrice, askQuantity);
    }

    private void SeedBookFromQuote(
        ReadOnlySpan<byte> symbolBytes,
        sbyte priceExponent,
        sbyte qtyExponent,
        decimal bidPrice,
        decimal bidQuantity,
        decimal askPrice,
        decimal askQuantity)
    {
        InstrumentRuntime? runtime = FindRuntime(symbolBytes);
        if (runtime is not null && !runtime.OrderBook.IsSynchronized)
        {
            runtime.SetEstimatedBookPrecision(priceExponent, qtyExponent);
            Instrument instrument = runtime.Instrument;
            long bidTicks = instrument.ToPriceTicks(bidPrice);
            long askTicks = instrument.ToPriceTicks(askPrice);
            long bidUnits = instrument.ToQuantityUnits(bidQuantity);
            long askUnits = instrument.ToQuantityUnits(askQuantity);
            if (instrument.Price(bidTicks) == bidPrice && instrument.Price(askTicks) == askPrice &&
                instrument.Quantity(bidUnits) == bidQuantity && instrument.Quantity(askUnits) == askQuantity)
            {
                runtime.SeedEstimatedBook(bidTicks, bidUnits, askTicks, askUnits, quoteDecoder.BookUpdateId);
            }
        }
    }

    private void PublishQuote(
        ReadOnlySpan<byte> symbolBytes,
        decimal bidPrice,
        decimal bidQuantity,
        decimal askPrice,
        decimal askQuantity)
    {
        string symbol = Encoding.UTF8.GetString(symbolBytes);
        var quote = new BestQuote(
            symbol,
            bidPrice,
            bidQuantity,
            askPrice,
            askQuantity,
            quoteDecoder.BookUpdateId,
            quoteDecoder.EventTime);
        QuoteReceived?.Invoke(quote);
        if (logEvents)
        {
            Console.WriteLine(
                $"QUOTE {symbol} bid={bidPrice} ({bidQuantity}) ask={askPrice} ({askQuantity}) update={quote.UpdateId}");
        }
    }

    private void DecodeDepthDiff(DirectBuffer buffer)
    {
        long decodeStart = Benchmark is null ? 0 : Stopwatch.GetTimestamp();
        depthDecoder.WrapForDecode(buffer, MessageHeader.Size, header.BlockLength, header.Version);
        sbyte priceExponent = depthDecoder.PriceExponent;
        sbyte qtyExponent = depthDecoder.QtyExponent;
        if (!ReadDepthChanges(priceExponent, qtyExponent, out int bidCount, out int askCount))
        {
            return;
        }

        if (depthDecoder.SymbolLength() > 64)
        {
            return;
        }

        Span<byte> symbolBytes = stackalloc byte[64];
        int symbolLength = depthDecoder.GetSymbol(symbolBytes);
        InstrumentRuntime? runtime = FindRuntime(symbolBytes[..symbolLength]);
        Benchmark?.AddDecode(Stopwatch.GetTimestamp() - decodeStart);
        SeedBookFromDepth(runtime, priceExponent, qtyExponent, bidCount, askCount);
        ApplyDepthUpdate(runtime, bidCount, askCount);
        LogDepthChanges(symbolBytes[..symbolLength], bidCount, askCount);
    }

    private bool ReadDepthChanges(sbyte priceExponent, sbyte qtyExponent, out int bidCount, out int askCount)
    {
        bidCount = 0;
        askCount = 0;
        var bids = depthDecoder.Bids;
        while (bids.HasNext)
        {
            var bid = bids.Next();
            if (bidCount == bidChanges.Length)
            {
                return false;
            }

            bidChanges[bidCount++] = new DepthLevel(DecodeDecimal(bid.Price, priceExponent), DecodeDecimal(bid.Qty,
                qtyExponent));
        }

        var asks = depthDecoder.Asks;
        while (asks.HasNext)
        {
            var ask = asks.Next();
            if (askCount == askChanges.Length)
            {
                return false;
            }

            askChanges[askCount++] = new DepthLevel(DecodeDecimal(ask.Price, priceExponent), DecodeDecimal(ask.Qty,
                qtyExponent));
        }

        return true;
    }

    private void SeedBookFromDepth(InstrumentRuntime? runtime, sbyte priceExponent, sbyte qtyExponent, int bidCount,
        int askCount)
    {
        if (EstimatesBook && runtime is not null && !runtime.OrderBook.IsSynchronized)
        {
            runtime.SetEstimatedBookPrecision(priceExponent, qtyExponent);
            for (int i = 0; i < bidCount && !runtime.OrderBook.IsSynchronized; i++)
            {
                DepthLevel level = bidChanges[i];
                if (level.Price > 0 && level.Quantity > 0)
                {
                    runtime.ObserveDepthForEstimatedBook(
                        true,
                        runtime.Instrument.ToPriceTicks(level.Price),
                        runtime.Instrument.ToQuantityUnits(level.Quantity),
                        depthDecoder.LastBookUpdateId);
                }
            }

            for (int i = 0; i < askCount && !runtime.OrderBook.IsSynchronized; i++)
            {
                DepthLevel level = askChanges[i];
                if (level.Price > 0 && level.Quantity > 0)
                {
                    runtime.ObserveDepthForEstimatedBook(
                        false,
                        runtime.Instrument.ToPriceTicks(level.Price),
                        runtime.Instrument.ToQuantityUnits(level.Quantity),
                        depthDecoder.LastBookUpdateId);
                }
            }
        }
    }

    private void ApplyDepthUpdate(InstrumentRuntime? runtime, int bidCount, int askCount)
    {
        bool wasSynchronized = runtime?.OrderBook.IsSynchronized ?? false;
        long bookStart = Benchmark is null ? 0 : Stopwatch.GetTimestamp();
        if (runtime is not null && runtime.BeginDepth(depthDecoder.FirstBookUpdateId, depthDecoder.LastBookUpdateId))
        {
            bool valid = ApplyDepthLevels(runtime, bidCount, askCount);
            CompleteDepthUpdate(runtime, valid, bookStart);
        }
        else if (runtime is not null && wasSynchronized && !runtime.OrderBook.IsSynchronized)
        {
            Benchmark?.AddBook(Stopwatch.GetTimestamp() - bookStart);
            EmitDecision(runtime, runtime.Evaluate());
            if (!IsReplay)
            {
                throw new IOException(
                    $"Depth sequence gap for {runtime.Instrument.Symbol}; reconnect and reload snapshot.");
            }
        }
    }

    private static bool ApplyDepthLevel(InstrumentRuntime runtime, DepthLevel level, bool bid)
    {
        long price = runtime.Instrument.ToPriceTicks(level.Price);
        long quantity = runtime.Instrument.ToQuantityUnits(level.Quantity);
        return runtime.Instrument.Price(price) == level.Price &&
            runtime.Instrument.Quantity(quantity) == level.Quantity && (bid ? runtime.ApplyBid(price,
            quantity) : runtime.ApplyAsk(price, quantity));
    }

    private bool ApplyDepthLevels(InstrumentRuntime runtime, int bidCount, int askCount)
    {
        bool valid = true;
        for (int i = 0; i < bidCount && valid; i++)
        {
            valid = ApplyDepthLevel(runtime, bidChanges[i], true);
        }

        for (int i = 0; i < askCount && valid; i++)
        {
            valid = ApplyDepthLevel(runtime, askChanges[i], false);
        }

        return valid;
    }

    private void CompleteDepthUpdate(InstrumentRuntime runtime, bool valid, long bookStart)
    {
        if (!valid)
        {
            runtime.Invalidate();
        }

        Benchmark?.AddBook(Stopwatch.GetTimestamp() - bookStart);
        QuoteDecision decision = valid ? runtime.EndDepth(checked(depthDecoder.EventTime * 1_000)) : runtime.Evaluate();
        if (valid)
        {
            Benchmark?.AddSignalAndQuote(runtime.LastSignalTicks, runtime.LastQuoteTicks);
        }

        EmitDecision(runtime, decision);
        if (runtime.SignalMemory.Faulted && !IsReplay)
        {
            throw new IOException(
                $"Signal history exhausted for {runtime.Instrument.Symbol}; reconnect and reload snapshot.");
        }

        if (!runtime.OrderBook.IsSynchronized && !IsReplay)
        {
            throw new IOException(
                $"Depth coverage lost for {runtime.Instrument.Symbol}; reconnect and reload snapshot.");
        }
    }

    private void LogDepthChanges(ReadOnlySpan<byte> symbolBytes, int bidCount, int askCount)
    {
        if (!logEvents)
        {
            return;
        }

        string symbol = Encoding.UTF8.GetString(symbolBytes);
        Console.WriteLine($"DEPTH {symbol} " + $"{depthDecoder.FirstBookUpdateId}" +
            $"->{depthDecoder.LastBookUpdateId} " + $"bids={bidCount} " + $"asks={askCount}");
        for (int i = 0; i < bidCount; i++)
        {
            DepthLevel bid = bidChanges[i];
            Console.WriteLine($"  BID CHANGE price={bid.Price} qty={bid.Quantity}");
        }

        for (int i = 0; i < askCount; i++)
        {
            DepthLevel ask = askChanges[i];
            Console.WriteLine($"  ASK CHANGE price={ask.Price} qty={ask.Quantity}");
        }
    }

    private void DecodeTrades(DirectBuffer buffer)
    {
        long decodeStart = Benchmark is null ? 0 : Stopwatch.GetTimestamp();
        tradesDecoder.WrapForDecode(buffer, MessageHeader.Size, header.BlockLength, header.Version);
        int count = 0;
        var group = tradesDecoder.Trades;
        while (group.HasNext)
        {
            var trade = group.Next();
            if (count == tradeQuantities.Length)
            {
                return;
            }

            tradeQuantities[count] = DecodeDecimal(trade.Qty, tradesDecoder.QtyExponent);
            tradeAggressors[count++] = trade.IsBuyerMaker == BoolEnum.False;
        }

        if (tradesDecoder.SymbolLength() > 64)
        {
            return;
        }

        Span<byte> symbol = stackalloc byte[64];
        int length = tradesDecoder.GetSymbol(symbol);
        InstrumentRuntime? runtime = FindRuntime(symbol[..length]);
        Benchmark?.AddDecode(Stopwatch.GetTimestamp() - decodeStart);
        if (runtime is null)
        {
            return;
        }

        ApplyTrades(runtime, count);
    }

    private void ApplyTrades(InstrumentRuntime runtime, int count)
    {
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
            Benchmark?.AddSignalAndQuote(runtime.LastSignalTicks, runtime.LastQuoteTicks);
            EmitDecision(runtime, decision);
            if (runtime.SignalMemory.Faulted && !IsReplay)
            {
                throw new IOException(
                    $"Trade history exhausted for {runtime.Instrument.Symbol}; reconnect and reload snapshot.");
            }
        }
    }

    private void EmitDecision(InstrumentRuntime runtime, in QuoteDecision decision)
    {
        Benchmark?.Decision(runtime, decision);
        DecisionProduced?.Invoke(runtime, decision);
    }

    private InstrumentRuntime? FindRuntime(ReadOnlySpan<byte> symbol)
    {
        for (int i = 0; i < runtimeSymbols.Length; i++)
        {
            if (symbol.SequenceEqual(runtimeSymbols[i]))
            {
                return runtimes[i];
            }
        }

        return null;
    }

    private static bool ValidFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 8)
        {
            return false;
        }

        int block = BinaryPrimitives.ReadUInt16LittleEndian(frame);
        int template = BinaryPrimitives.ReadUInt16LittleEndian(frame[2..]);
        int schema = BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]);
        int version = BinaryPrimitives.ReadUInt16LittleEndian(frame[6..]);
        if (schema != 1 || version != 0 || frame.Length < 8 + block + 1)
        {
            return false;
        }

        int offset = 8 + block;
        if (!SkipFrameGroups(frame, template, block, ref offset))
        {
            return false;
        }

        return offset < frame.Length && offset + 1 + frame[offset] == frame.Length;
    }

    private static bool SkipFrameGroups(ReadOnlySpan<byte> frame, int template, int block, ref int offset)
    {
        if (template == BestBidAskStreamEvent.TemplateId)
        {
            return block >= BestBidAskStreamEvent.BlockLength;
        }

        if (template == DepthDiffStreamEvent.TemplateId)
        {
            return block >= DepthDiffStreamEvent.BlockLength && SkipDepthGroups(frame, ref offset);
        }

        if (template == TradesStreamEvent.TemplateId)
        {
            return block >= TradesStreamEvent.BlockLength && frame.Length - offset >= 6 && SkipTradeGroup(frame,
                ref offset);
        }

        return false;
    }

    private static bool SkipDepthGroups(ReadOnlySpan<byte> frame, ref int offset)
    {
        for (int i = 0; i < 2; i++)
        {
            if (frame.Length - offset < 4)
            {
                return false;
            }

            int groupBlock = BinaryPrimitives.ReadUInt16LittleEndian(frame[offset..]);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(frame[(offset + 2)..]);
            if (groupBlock < 16 || count > 5_000)
            {
                return false;
            }

            offset += 4;
            if ((long)groupBlock * count > frame.Length - offset)
            {
                return false;
            }

            offset += groupBlock * count;
        }

        return true;
    }

    private static bool SkipTradeGroup(ReadOnlySpan<byte> frame, ref int offset)
    {
        int groupBlock = BinaryPrimitives.ReadUInt16LittleEndian(frame[offset..]);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(frame[(offset + 2)..]);
        if (groupBlock < 25 || count > 8_192)
        {
            return false;
        }

        offset += 6;
        if ((long)groupBlock * count > frame.Length - offset)
        {
            return false;
        }

        offset += groupBlock * (int)count;
        return true;
    }

    private static string FormatStreams(string[]? symbolsToFormat) => string.Join("/",
        symbolsToFormat?.SelectMany(StreamsForSymbol) ?? []);

    private static string[] StreamsForSymbol(string symbol)
    {
        string name = symbol.ToLowerInvariant();
        return[$"{name}@depth", $"{name}@bestBidAsk", $"{name}@trade"];
    }

    private static decimal DecodeDecimal(long mantissa, sbyte exponent)
    {
        decimal value = mantissa;
        if (exponent > 0)
        {
            for (int i = 0; i < exponent; i++)
            {
                value *= 10m;
            }
        }
        else
        {
            for (int i = 0; i < -exponent; i++)
            {
                value /= 10m;
            }
        }

        return value;
    }

    public readonly record struct WsMessage(WebSocketMessageType Type, byte[] Payload, int Length,
        long ReceivedTimestamp = 0);

    public readonly record struct RecordedMessage(Guid RecordingId, long SequenceNumber, long ElapsedNanoseconds,
        short MessageType, byte[] Payload);
}
