using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using AssumptionZero.Infrastructure.Simulation;
using static AssumptionZero.Infrastructure.Simulation.ExchangeProtocol;
namespace AssumptionZero.Simulator;

public sealed class ClosedLoopSession(Recording recording, SimManifest manifest, double seconds)
{
    private readonly TaskCompletionSource<WebSocket> market = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<WebSocket> orders = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<ExchangePacket> incoming = Channel.CreateBounded<ExchangePacket>(new BoundedChannelOptions(Capacity) { SingleReader = true, SingleWriter = true });
    public SimulatedExchange Exchange { get; } = new(manifest);
    public List<long> SenderLateness { get; } = new();
    public WebApplication Create()
    {
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build(); app.UseWebSockets();
        app.MapGet("/market", c => Accept(c, market, false)); app.MapGet("/orders", c => Accept(c, orders, true)); return app;
    }
    private async Task Accept(HttpContext context, TaskCompletionSource<WebSocket> target, bool receive)
    {
        if (!context.WebSockets.IsWebSocketRequest || target.Task.IsCompleted) { context.Response.StatusCode = 409; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        if (!target.TrySetResult(socket)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        Task reader = receive ? ReadOrders(socket, linked.Token) : Task.CompletedTask;
        try { await done.Task.WaitAsync(context.RequestAborted); }
        finally { await linked.CancelAsync(); socket.Abort(); try { await reader; } catch (OperationCanceledException) { } catch (WebSocketException) { } }
    }
    private async Task ReadOrders(WebSocket socket, CancellationToken ct)
    {
        var receiveScratch = new byte[16384];
        try
        {
            while (await Receive(socket, ct, receiveScratch) is { } bytes)
            {
                var p = Packet(bytes);
                if (p.Kind is not ("commands" or "finish" or "settled")) throw new InvalidDataException("Unsupported client packet.");
                if (!incoming.Writer.TryWrite(p)) throw new IOException("Exchange command queue overflow.");
            }
            incoming.Writer.TryComplete(new IOException("Order connection closed before session completion."));
        }
        catch (Exception e) { incoming.Writer.TryComplete(e); }
    }
    private async Task Reports(WebSocket socket, List<ExecutionEvent> events, CancellationToken ct)
    { if (events.Count > 0) await Send(socket, new("reports", Events: events.ToArray()), ct); }
    private static async Task Frame(WebSocket socket, long step, long watermark, byte[] payload, CancellationToken ct)
    {
        byte[] data = new byte[24 + payload.Length]; BinaryPrimitives.WriteInt64LittleEndian(data, step);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(8), watermark);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(16), Stopwatch.GetTimestamp()); payload.CopyTo(data, 24);
        await socket.SendAsync(data.AsMemory(), WebSocketMessageType.Binary, true, ct);
    }
    public async Task Run(CancellationToken ct)
    {
        try
        {
            var os = await orders.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            await Send(os, new("hello", Manifest: manifest), ct);
            var ms = await market.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (manifest.Mode == "deterministic")
            {
                foreach (var m in recording.Messages)
                {
                    await Reports(os, Exchange.Advance(m.SequenceNumber), ct);
                    await Frame(ms, m.SequenceNumber, Exchange.Sequence, m.Payload, ct);
                    var batch = await incoming.Reader.ReadAsync(ct);
                    if (batch.Kind != "commands" || batch.Step != m.SequenceNumber) throw new InvalidDataException("Replay barrier mismatch.");
                    await Reports(os, Exchange.Commands(batch.Commands ?? [], m.SequenceNumber), ct);
                }
                await Frame(ms, 0, Exchange.Sequence, [], ct);
                var finish = await incoming.Reader.ReadAsync(ct);
                if (finish.Kind != "finish") throw new InvalidDataException("Missing client finish barrier.");
                await Reports(os, Exchange.Finish(), ct);
            }
            else
            {
                var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var worker = ProcessAsyncOrders(os, finish, ct);
                long origin = Stopwatch.GetTimestamp();
                foreach (var m in recording.Messages)
                {
                    long target = origin + (long)(m.ElapsedNanoseconds / (double)recording.DurationNanoseconds * seconds * Stopwatch.Frequency);
                    while (true)
                    {
                        long left = target - Stopwatch.GetTimestamp(); if (left <= 0) break;
                        if (left > Stopwatch.Frequency / 500) await Task.Delay(TimeSpan.FromSeconds((double)left / Stopwatch.Frequency - 0.001), ct);
                        else { ct.ThrowIfCancellationRequested(); Thread.SpinWait(32); }
                    }
                    SenderLateness.Add(Math.Max(0, Stopwatch.GetTimestamp() - target));
                    await Frame(ms, m.SequenceNumber, 0, m.Payload, ct);
                }
                await Frame(ms, 0, 0, [], ct);
                await finish.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
                await worker;
            }
            await Frame(ms, -1, Exchange.Sequence, [], ct);
            var settled = await incoming.Reader.ReadAsync(ct);
            if (settled.Kind != "settled") throw new InvalidDataException("Missing final reconciliation acknowledgement.");
            done.TrySetResult();
        }
        catch (Exception e) { done.TrySetException(e); throw; }
    }
    private async Task ProcessAsyncOrders(WebSocket socket, TaskCompletionSource finished, CancellationToken ct)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            while (true)
            {
                // Synthetic delay units are milliseconds in paced mode, replay steps in deterministic mode.
                await Reports(socket, Exchange.Advance(clock.ElapsedMilliseconds), ct);
                while (incoming.Reader.TryRead(out var p))
                {
                    if (p.Kind == "finish")
                    { await Reports(socket, Exchange.Finish(), ct); finished.TrySetResult(); return; }
                    if (p.Kind != "commands") throw new InvalidDataException("Unexpected asynchronous command packet.");
                    await Reports(socket, Exchange.Commands(p.Commands ?? [], clock.ElapsedMilliseconds), ct);
                }
                if (incoming.Reader.Completion.IsCompleted) await incoming.Reader.Completion;
                await Task.Delay(1, ct);
            }
        }
        catch (Exception e) { finished.TrySetException(e); throw; }
    }
    public static (string Symbol, sbyte Price, sbyte Quantity) Metadata(byte[] b)
    {
        if (b.Length < 27) throw new InvalidDataException("Short SBE message.");
        int U16(int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p, 2));
        int template = U16(2), offset = 8 + U16(0);
        if (U16(4) != 1 || U16(6) != 0) throw new InvalidDataException("Unsupported market schema.");
        if (template == 10000) offset = checked(offset + 6 + U16(offset) * (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset + 2, 4)));
        else if (template is 10002 or 10003) for (int i = 0; i < 2; i++) offset = checked(offset + 4 + U16(offset) * U16(offset + 2));
        else if (template != 10001) throw new InvalidDataException("Unsupported market template.");
        if (offset >= b.Length || offset + 1 + b[offset] != b.Length) throw new InvalidDataException("Invalid SBE symbol boundary.");
        int exponent = template == 10003 ? 32 : 24;
        return (Encoding.UTF8.GetString(b, offset + 1, b[offset]), (sbyte)b[exponent], (sbyte)b[exponent + 1]);
    }
    public static SimManifest Manifest(Recording recording, string[] symbols, string mode, string scenario)
    {
        var units = new Dictionary<string, (sbyte Price, sbyte Quantity)>();
        long step = 0; long elapsed = -1;
        foreach (var m in recording.Messages)
        {
            if (m.SequenceNumber != ++step || m.ElapsedNanoseconds < elapsed) throw new InvalidDataException("Recording must be a contiguous, monotonic prefix.");
            elapsed = m.ElapsedNanoseconds; var v = Metadata(m.Payload);
            if (!symbols.Contains(v.Symbol)) throw new InvalidDataException("Recorded symbol is not configured.");
            if (units.TryGetValue(v.Symbol, out var prior) && prior != (v.Price, v.Quantity)) throw new InvalidDataException("Changing SBE precision is unsupported.");
            units.TryAdd(v.Symbol, (v.Price, v.Quantity));
        }
        decimal Unit(sbyte e) { if (e is < -28 or > 28) throw new InvalidDataException("Unsupported decimal precision."); decimal v = 1; for (int i = 0; i < Math.Abs((int)e); i++) v = e < 0 ? v / 10 : checked(v * 10); return v; }
        var specs = symbols.Select((s, i) => { var u = units.GetValueOrDefault(s); return new SimInstrument(i, s, Money(Unit(u.Price)), Money(Unit(u.Quantity))); }).ToArray();
        string[] assets = ["USDT", .. symbols.Select(s => s[..^4])];
        return new(ExchangeProtocol.Version, Guid.NewGuid().ToString("N"), mode, recording.Messages.Count, recording.Messages.Count / 10, "0.001", specs, assets,
            assets.Select((_, i) => i == 0 ? "1000000000" : "100").ToArray(), scenario);
    }
}
