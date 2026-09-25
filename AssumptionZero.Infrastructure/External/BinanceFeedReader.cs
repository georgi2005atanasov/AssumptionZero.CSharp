using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading.Channels;
using Npgsql;
using NpgsqlTypes;
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
    private static long binaryMessages = 0;
    private static string[]? symbols;
    private readonly string apiKey;
    private readonly Uri uri;

    private readonly MessageHeader header = new();
    private readonly DepthDiffStreamEvent depthDecoder = new();
    private readonly BestBidAskStreamEvent quoteDecoder = new();

    private readonly List<DepthLevel> bidUpdates = new();
    private readonly List<DepthLevel> askUpdates = new();

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

    public BinanceFeedReader(string apiKey, string[] symbols, string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        this.apiKey = apiKey;
        BinanceFeedReader.symbols = symbols;
        string streams = FormatStreams(BinanceFeedReader.symbols);
        uri = new Uri($"{baseUrl}{streams}");
    }

    public async Task Start(
        CancellationToken cancellationToken = default,
        ExecutionMode executionMode = ExecutionMode.SeedData,
        string connectionString = "")
    {
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
            recordingStartTimestamp = Stopwatch.GetTimestamp();
            await connectedClient.AndStartReceiving(
                message => ProcessingPipeline(message, executionMode),
                cancellationToken);
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
        binaryMessages++;

        if (mode == ExecutionMode.SeedData)
        {
            long now = Stopwatch.GetTimestamp();

            var recorded = new RecordedMessage(
                recordingId,
                ++recordingSequence,
                Stopwatch.GetElapsedTime(recordingStartTimestamp, now).Ticks * 100L,
                (short)message.Type,
                (byte[])message.Payload.Clone());

            if (!recordedMessages.Writer.TryWrite(recorded))
                throw new InvalidOperationException(
                    "Recording queue is full. Stop the capture to avoid losing messages.");

            return;
        }

        DecodeSbeMessage(
            message.Payload,
            binaryMessages);
    }

    private ClientWebSocket PrepareClient()
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader(
            BinanceConstants.BinanceApiKeyHeaderName,
            apiKey
        );
        ws.Options.KeepAliveInterval = TimeSpan.Zero;
        return ws;
    }

    private void DecodeSbeMessage(
        byte[] payload,
        long messageNumber)
    {
        if (payload.Length < MessageHeader.Size)
        {
            Console.WriteLine(
                $"[{messageNumber}] Invalid SBE message: " +
                $"{payload.Length} bytes.");
            return;
        }

        var buffer = new DirectBuffer(payload);
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
            DecodeDepthDiff(buffer, messageNumber);
            return;
        }

        Console.WriteLine(
            $"[{messageNumber}] Unknown SBE template " +
            $"{header.TemplateId}, " +
            $"schema={header.SchemaId}:{header.Version}");
    }

    private void DecodeBestBidAsk(DirectBuffer buffer)
    {
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
        string symbol = quoteDecoder.GetSymbol();
        var quote = new BestQuote(
            symbol,
            bidPrice,
            bidQuantity,
            askPrice,
            askQuantity,
            quoteDecoder.BookUpdateId,
            quoteDecoder.EventTime);
        // The handler at the top of this file logs the actual prices.
        QuoteReceived?.Invoke(quote);
    }

    private void DecodeDepthDiff(
        DirectBuffer buffer,
        long messageNumber)
    {
        depthDecoder.WrapForDecode(
            buffer,
            MessageHeader.Size,
            header.BlockLength,
            header.Version);
        sbyte priceExponent = depthDecoder.PriceExponent;
        sbyte qtyExponent = depthDecoder.QtyExponent;
        bidUpdates.Clear();
        askUpdates.Clear();
        var bids = depthDecoder.Bids;
        while (bids.HasNext)
        {
            var bid = bids.Next();

            bidUpdates.Add(new DepthLevel(
                DecodeDecimal(bid.Price, priceExponent),
                DecodeDecimal(bid.Qty, qtyExponent)));
        }

        var asks = depthDecoder.Asks;

        while (asks.HasNext)
        {
            var ask = asks.Next();

            askUpdates.Add(new DepthLevel(
                DecodeDecimal(ask.Price, priceExponent),
                DecodeDecimal(ask.Qty, qtyExponent)));
        }

        // In this SBE message, Symbol follows the repeating groups.
        string symbol = depthDecoder.GetSymbol();
        Console.WriteLine(
            $"DEPTH {symbol} " +
            $"{depthDecoder.FirstBookUpdateId}" +
            $"->{depthDecoder.LastBookUpdateId} " +
            $"bids={bidUpdates.Count} " +
            $"asks={askUpdates.Count}");
        foreach (DepthLevel bid in bidUpdates)
        {
            Console.WriteLine(
                $"  BID CHANGE price={bid.Price} qty={bid.Quantity}");
        }

        foreach (DepthLevel ask in askUpdates)
        {
            Console.WriteLine(
                $"  ASK CHANGE price={ask.Price} qty={ask.Quantity}");
        }
    }

    private static string FormatStreams(string[]? symbolsToFormat) => string.Join(
        "/",
        symbolsToFormat?.SelectMany(symbol =>
        {
            string name = symbol.ToLowerInvariant();

            return new[]
            {
                $"{name}@depth",
                $"{name}@bestBidAsk"
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
        byte[] Payload);

    public readonly record struct RecordedMessage(
        Guid RecordingId,
        long SequenceNumber,
        long ElapsedNanoseconds,
        short MessageType,
        byte[] Payload);
}