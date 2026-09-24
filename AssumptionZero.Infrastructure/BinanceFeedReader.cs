using System.Net.WebSockets;
using Org.SbeTool.Sbe.Dll;
using Spot_stream;

namespace AssumptionZero.Infrastructure;

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
        CancellationToken cancellationToken = default)
        => await (await PrepareClient()
                .ThenConnect(symbols ?? [], uri, cancellationToken))
            .AndStartReceiving(Messages, cancellationToken);

    private void Messages(WsMessage message)
    {
        binaryMessages++;
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
}