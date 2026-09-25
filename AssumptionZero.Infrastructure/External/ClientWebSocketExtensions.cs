using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AssumptionZero.Infrastructure.External;

public static class ClientWebSocketExtensions
{
    public static async Task<ClientWebSocket> ThenConnect(
        this ClientWebSocket ws,
        string[] symbols,
        Uri uri,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"Connecting to SBE feed for {symbols?.Length} instruments...");
        await ws.ConnectAsync(uri, cancellationToken);
        Console.WriteLine("SBE connection open.");
        Console.WriteLine("Waiting for depth and best bid/ask updates...");
        return ws;
    }

    public static async Task<ClientWebSocket> AndStartReceiving(
        this ClientWebSocket ws,
        Action<BinanceFeedReader.WsMessage> makeDecisions,
        CancellationToken cancellationToken,
        bool allowNormalClosure = false,
        bool logEvents = true)
    {
        byte[] chunk = new byte[64 * 1024];

        while (ws.State == WebSocketState.Open &&
               !cancellationToken.IsCancellationRequested)
        {
            BinanceFeedReader.WsMessage message = await ReceiveMessage(
                ws,
                chunk,
                cancellationToken);
            if (message.Type == WebSocketMessageType.Close)
            {
                if (allowNormalClosure && ws.CloseStatus == WebSocketCloseStatus.NormalClosure)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Replay received", cancellationToken);
                    break;
                }
                throw new IOException($"Binance closed the connection: {ws.CloseStatus}; {ws.CloseStatusDescription}");
            }

            if (message.Type == WebSocketMessageType.Text)
            {
                HandleTextMessage(message.Payload, logEvents);
                continue;
            }

            if (message.Type != WebSocketMessageType.Binary)
                continue;

            makeDecisions(message);
        }

        return ws;
    }

    private static async Task<BinanceFeedReader.WsMessage> ReceiveMessage(
        ClientWebSocket ws,
        byte[] chunk,
        CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await ws.ReceiveAsync(
                new ArraySegment<byte>(chunk),
                cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new BinanceFeedReader.WsMessage(
                    WebSocketMessageType.Close,
                    Array.Empty<byte>());
            }

            if (result.Count > 0)
            {
                stream.Write(
                    chunk,
                    0,
                    result.Count);
            }

            if (stream.Length > 1024 * 1024)
            {
                throw new InvalidDataException(
                    "WebSocket message exceeded 1 MB.");
            }
        } while (!result.EndOfMessage);

        return new BinanceFeedReader.WsMessage(
            result.MessageType,
            stream.ToArray());
    }

    private static void HandleTextMessage(byte[] payload, bool logEvents)
    {
        string text = Encoding.UTF8.GetString(payload);

        if (logEvents) Console.WriteLine($"TEXT: {text}");

        using var json = JsonDocument.Parse(payload);

        if (json.RootElement.TryGetProperty("e", out var eventType) &&
            eventType.GetString() == "serverShutdown")
        {
            throw new IOException(
                "Binance announced a server shutdown.");
        }

        if (json.RootElement.TryGetProperty("code", out _))
        {
            throw new IOException(
                $"Binance returned an error: {text}");
        }
    }
}
