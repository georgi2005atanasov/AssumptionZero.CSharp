using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading.Channels;
using AssumptionZero.Infrastructure;
using AssumptionZero.Infrastructure.External;
using AssumptionZero.Simulator;
using Microsoft.AspNetCore.Builder;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.SbeTool.Sbe.Dll;
using Spot_stream;

namespace AssumptionZero.Tests;

[TestClass]
public class WebSocketReplayTests
{
    public TestContext TestContext { get; set; } = null!;

    private sealed class RunningServer(WebApplication app, Channel<ReplayReport> reports) : IAsyncDisposable
    {
        public Uri Uri => new(app.Urls.Single().Replace("http://", "ws://") + "/stream?streams=ignored@depth");

        public async Task<ReplayReport> Report() =>
            await reports.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        public static async Task<RunningServer> Start(Recording recording)
        {
            var reports = Channel.CreateUnbounded<ReplayReport>();
            var app = ReplayServer.Create(recording, "http://127.0.0.1:0", report => reports.Writer.TryWrite(report));
            await app.StartAsync();
            return new(app, reports);
        }
    }

    private static async Task<List<(byte[] Payload, WebSocketMessageType Type, long Receipt)>> ReceiveAll(Uri uri)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(uri, timeout.Token);
        var messages = new List<(byte[], WebSocketMessageType, long)>();
        var chunk = new byte[4096];
        while (true)
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(chunk.AsMemory(), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Assert.AreEqual(WebSocketCloseStatus.NormalClosure, socket.CloseStatus);
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Received", timeout.Token);
                    return messages;
                }

                message.Write(chunk.AsSpan(0, result.Count));
            } while (!result.EndOfMessage);

            long receipt = Stopwatch.GetTimestamp();
            messages.Add((message.ToArray(), result.MessageType, receipt));
        }
    }

    [TestMethod]
    public async Task PreservesBytesTypesBoundariesOrderAndRepeatedRuns()
    {
        var payload = Enumerable.Range(0, 70_000).Select(i => (byte)i).ToArray();
        var recording = new Recording(Guid.NewGuid(),
        [
            new(1, 20_000_000, WebSocketMessageType.Binary, [1, 2, 3]),
            new(2, 20_000_000, WebSocketMessageType.Text, "{}"u8.ToArray()),
            new(3, 20_000_001, WebSocketMessageType.Binary, payload)
        ]);
        await using var server = await RunningServer.Start(recording);
        for (int run = 0; run < 2; run++)
        {
            var received = await ReceiveAll(server.Uri);
            var report = await server.Report();
            Assert.IsTrue(report.Completed);
            Assert.AreEqual(recording.PayloadBytes, report.SentBytes);
            Assert.AreEqual(recording.Messages.Count, received.Count);
            for (int i = 0; i < received.Count; i++)
            {
                CollectionAssert.AreEqual(recording.Messages[i].Payload, received[i].Payload);
                Assert.AreEqual(recording.Messages[i].MessageType, received[i].Type);
            }
        }
    }

    [TestMethod]
    public async Task RejectsConcurrentClientsAndCancelsDuringLongInitialGap()
    {
        await using var server = await RunningServer.Start(ReplayEngineTests.Capture(60_000_000_000));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var first = new ClientWebSocket();
        await first.ConnectAsync(server.Uri, timeout.Token);
        using var second = new ClientWebSocket();
        await Assert.ThrowsExceptionAsync<WebSocketException>(() => second.ConnectAsync(server.Uri, timeout.Token));
        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "Cancel replay", timeout.Token);
        var report = await server.Report();
        Assert.IsFalse(report.Completed);
        Assert.AreEqual(0, report.SentMessages);
        // The slot must also be reusable after an interrupted run.
        using var next = new ClientWebSocket();
        await next.ConnectAsync(server.Uri, timeout.Token);
        await next.CloseAsync(WebSocketCloseStatus.NormalClosure, "Cancel again", timeout.Token);
        Assert.IsFalse((await server.Report()).Completed);
    }

    [TestMethod]
    public async Task AbruptDisconnectCancelsDuringLongGap()
    {
        await using var server = await RunningServer.Start(ReplayEngineTests.Capture(60_000_000_000));
        using var client = new ClientWebSocket();
        await client.ConnectAsync(server.Uri, CancellationToken.None);
        client.Abort();
        var report = await server.Report();
        Assert.IsFalse(report.Completed);
        Assert.AreEqual(0, report.SentMessages);
    }

    internal static byte[] QuotePayload()
    {
        var bytes = new byte[128];
        var encoder =
            new BestBidAskStreamEvent().WrapForEncodeAndApplyHeader(new DirectBuffer(bytes), 0, new MessageHeader());
        encoder.EventTime = 1_700_000_000_123_456;
        encoder.BookUpdateId = 42;
        encoder.PriceExponent = -2;
        encoder.QtyExponent = -3;
        encoder.BidPrice = 12345;
        encoder.AskPrice = 12346;
        encoder.BidQty = 2500;
        encoder.AskQty = 3000;
        encoder.SetSymbol("BTCUSDT");
        return bytes[..encoder.Limit];
    }

    [TestMethod]
    public async Task ReplayAndLivePathsDecodeSameSbeQuoteAndKeepLiveClosureFailure()
    {
        var recording = new Recording(Guid.NewGuid(), [new(1, 0, WebSocketMessageType.Binary, QuotePayload())]);
        await using var server = await RunningServer.Start(recording);
        var quotes = new List<BestQuote>();
        var replay = new BinanceFeedReader("", [], server.Uri.ToString(), FeedMode.ReplayWithSnapshot, logEvents: false);
        replay.QuoteReceived += quotes.Add;
        await replay.Start().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue((await server.Report()).Completed);
        var live = new BinanceFeedReader("test-key", [], server.Uri.ToString(), FeedMode.Live, logEvents: false);
        live.QuoteReceived += quotes.Add;
        await Assert.ThrowsExceptionAsync<IOException>(() => live.Start());
        await server.Report();
        Assert.AreEqual(2, quotes.Count);
        Assert.AreEqual(quotes[0], quotes[1]);
        Assert.AreEqual(new BestQuote("BTCUSDT", 123.45m, 2.5m, 123.46m, 3m, 42, 1_700_000_000_123_456), quotes[0]);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task DepthDecodingPreservesUpdatesAndReplayLoggingDefaultsOff()
    {
        var bytes = new byte[256];
        var encoder =
            new DepthDiffStreamEvent().WrapForEncodeAndApplyHeader(new DirectBuffer(bytes), 0, new MessageHeader());
        encoder.EventTime = 123;
        encoder.FirstBookUpdateId = 40;
        encoder.LastBookUpdateId = 42;
        encoder.PriceExponent = 0;
        encoder.QtyExponent = 0;
        var bid = encoder.BidsCount(1).Next();
        bid.Price = 100;
        bid.Qty = 2;
        var ask = encoder.AsksCount(1).Next();
        ask.Price = 101;
        ask.Qty = 0;
        encoder.SetSymbol("BTCUSDT");
        var recording = new Recording(Guid.NewGuid(), [new(1, 0, WebSocketMessageType.Binary, bytes[..encoder.Limit])]);
        await using var server = await RunningServer.Start(recording);
        var originalOutput = Console.Out;
        try
        {
            using var output = new StringWriter();
            Console.SetOut(output);
            var quiet = new BinanceFeedReader("", [], server.Uri.ToString(), FeedMode.ReplayWithSnapshot);
            await quiet.Start();
            await server.Report();
            Assert.IsFalse(output.ToString().Contains("DEPTH"));

            output.GetStringBuilder().Clear();
            var verbose = new BinanceFeedReader("", [], server.Uri.ToString(), FeedMode.ReplayWithSnapshot, logEvents: true);
            await verbose.Start();
            await server.Report();
            string replayOutput = output.ToString();
            StringAssert.Contains(replayOutput, "DEPTH BTCUSDT 40->42 bids=1 asks=1");
            StringAssert.Contains(replayOutput, "BID CHANGE price=100 qty=2");
            StringAssert.Contains(replayOutput, "ASK CHANGE price=101 qty=0");

            output.GetStringBuilder().Clear();
            var live = new BinanceFeedReader("test", [], server.Uri.ToString(), FeedMode.Live, logEvents: true);
            await Assert.ThrowsExceptionAsync<IOException>(() =>
                live.Start());
            await server.Report();
            Assert.AreEqual(replayOutput, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
        }
    }

    [TestMethod]
    [TestCategory("Timing")]
    public async Task LoopbackReportsServerAndClientTimingSeparately()
    {
        const int count = 1000;
        var recording = new Recording(Guid.NewGuid(), Enumerable.Range(0, count).Select(i =>
            new ReplayMessage(i + 1, 50_000_000L + i * 100_000L, WebSocketMessageType.Binary, QuotePayload())));
        await using var server = await RunningServer.Start(recording);
        // Warm the send/receive paths with a complete unmeasured run.
        await ReceiveAll(server.Uri);
        await server.Report();
        var received = await ReceiveAll(server.Uri);
        var report = await server.Report();
        Assert.IsTrue(report.Completed);
        Assert.AreEqual(count, received.Count);
        var receiptLateness = received.Select((message, i) =>
            NanosecondTime.FromTicks(message.Receipt - report.OriginTimestamp, Stopwatch.Frequency)
            - recording.Messages[i].ElapsedNanoseconds).ToArray();
        var receiptIntervals = received.Skip(1).Select((message, i) => Math.Abs(
            NanosecondTime.FromTicks(message.Receipt - received[i].Receipt, Stopwatch.Frequency) - 100_000)).ToArray();
        TestContext.WriteLine($"Clock frequency: {Stopwatch.Frequency}; messages: {count}; spacing: 100000 ns");
        TestContext.WriteLine($"Server send-start lateness (ns): {report.SendStartLateness}");
        TestContext.WriteLine($"Server send duration (ns): {report.SendDuration}");
        TestContext.WriteLine($"Client receipt lateness (ns): {TimingStatistics.Calculate(receiptLateness, count)}");
        TestContext.WriteLine(
            $"Client absolute inter-receipt interval error (ns): {TimingStatistics.Calculate(receiptIntervals, count - 1)}");
    }
}
