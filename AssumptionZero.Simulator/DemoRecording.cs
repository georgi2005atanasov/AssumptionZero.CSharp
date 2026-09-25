using System.Net.WebSockets;
using Org.SbeTool.Sbe.Dll;
using Spot_stream;

namespace AssumptionZero.Simulator;

/// <summary>A small BTCUSDT replay that matches the bundled application replay fixture.</summary>
public static class DemoRecording
{
    public static Recording Create()
    {
        var messages = new ReplayMessage[120];
        for (int i = 0; i < messages.Length; i++)
        {
            var bytes = new byte[128];
            var encoder = new DepthDiffStreamEvent().WrapForEncodeAndApplyHeader(
                new DirectBuffer(bytes), 0, new MessageHeader());
            encoder.EventTime = i * 60_000L;
            encoder.FirstBookUpdateId = 40 + i;
            encoder.LastBookUpdateId = 40 + i;
            encoder.PriceExponent = -2;
            encoder.QtyExponent = 0;
            var bid = encoder.BidsCount(1).Next();
            bid.Price = 10_000;
            bid.Qty = 10 + i % 3;
            var ask = encoder.AsksCount(1).Next();
            ask.Price = 10_001;
            ask.Qty = 10;
            encoder.SetSymbol("BTCUSDT");
            messages[i] = new ReplayMessage(i + 1, i * 60_000_000L,
                WebSocketMessageType.Binary, bytes[..encoder.Limit]);
        }
        return new Recording(Guid.Parse("d3ca7abe-4290-4aa5-b775-1b44c80672e2"), messages);
    }
}
