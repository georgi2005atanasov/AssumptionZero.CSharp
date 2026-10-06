using System.Net.WebSockets;
using System.Text.Json;
using System.Globalization;

namespace AssumptionZero.Infrastructure.Simulation;

public sealed record SimInstrument(int Id, string Symbol, string Tick, string Step);
public sealed record SimManifest(int Version, string Session, string Mode, int Frames, int Warmup,
    string Fee, SimInstrument[] Instruments, string[] Assets, string[] InitialFree, string Scenario);
public sealed record OrderCommand(long Id, long Intent, int Instrument, bool Buy, string Action,
    long OrderId, long Price, long Quantity, long EventTime);
public sealed record BalanceValue(int Asset, string Free, string Locked);
public sealed record ExecutionEvent(long Sequence, long CommandId, long Intent, int Instrument, bool Buy,
    long OrderId, string Kind, int Status, long Filled, long Price, string Reason,
    BalanceValue[] Balances, long ServerTicks);
public sealed record ExchangePacket(string Kind, long Step = 0, OrderCommand[]? Commands = null,
    ExecutionEvent[]? Events = null, SimManifest? Manifest = null);
public static class ExchangeProtocol
{
    public const int Version = 1;
    public const int Capacity = 8192;
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    public static string Money(decimal d) => d.ToString("0.############################", CultureInfo.InvariantCulture);
    public static decimal Money(string d) => decimal.Parse(d, CultureInfo.InvariantCulture);
    public static async Task Send(WebSocket socket, ExchangePacket packet, CancellationToken ct) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(packet, Json).AsMemory(), WebSocketMessageType.Text, true, ct);
    public static async Task<byte[]?> Receive(WebSocket socket, CancellationToken ct, byte[]? scratch = null)
    {
        using var buffer = new MemoryStream();
        var chunk = scratch ?? new byte[16384];
        ValueWebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(chunk.AsMemory(), ct);
            if (part.MessageType == WebSocketMessageType.Close) return null;
            if (buffer.Length + part.Count > 2 * 1024 * 1024) throw new InvalidDataException("Exchange frame too large.");
            buffer.Write(chunk, 0, part.Count);
        } while (!part.EndOfMessage);
        return buffer.ToArray();
    }
    public static ExchangePacket Packet(byte[] data) => JsonSerializer.Deserialize<ExchangePacket>(data, Json)
        ?? throw new InvalidDataException("Empty exchange packet.");
}
