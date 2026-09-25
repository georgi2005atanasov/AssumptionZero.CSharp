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
        byte[] receiveBuffer,
        CancellationToken cancellationToken,
        bool allowNormalClosure = false,
        bool logEvents = true)
    {
        while (ws.State == WebSocketState.Open &&
               !cancellationToken.IsCancellationRequested)
        {
            int length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (length == receiveBuffer.Length)
                    throw new InvalidDataException("WebSocket message exceeded 1 MB.");
                result = await ws.ReceiveAsync(receiveBuffer.AsMemory(length), cancellationToken);
                length += result.Count;
            } while (!result.EndOfMessage && result.MessageType != WebSocketMessageType.Close);
            var message = new BinanceFeedReader.WsMessage(result.MessageType, receiveBuffer, length);
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
                HandleTextMessage(message.Payload.AsMemory(0, message.Length), logEvents);
                continue;
            }

            if (message.Type != WebSocketMessageType.Binary)
                continue;

            makeDecisions(message);
        }

        return ws;
    }

    private static void HandleTextMessage(ReadOnlyMemory<byte> payload, bool logEvents)
    {
        string text = Encoding.UTF8.GetString(payload.Span);

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
