using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.Entities;
using AssumptionZero.Domain.InMemory;
using AssumptionZero.Infrastructure;
using AssumptionZero.Infrastructure.External;
using AssumptionZero.Infrastructure.Simulation;
using static AssumptionZero.Infrastructure.Simulation.ExchangeProtocol;
namespace AssumptionZero.App;

public static class ClosedLoopClient
{
    public static async Task Run(string url, string output, CancellationToken stop)
    {
        bool measure = Environment.GetEnvironmentVariable("SIM_MEASURE") != "false";
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop); var ct = lifetime.Token;
        using var os = new ClientWebSocket(); using var ms = new ClientWebSocket();
        await os.ConnectAsync(new Uri(url + "/orders"), ct);
        var hello = Packet(await Receive(os, ct) ?? throw new IOException("No exchange manifest."));
        if (hello.Kind != "hello") throw new InvalidDataException("Expected manifest handshake.");
        var manifest = hello.Manifest ?? throw new InvalidDataException("Manifest missing.");
        if (manifest.Version != ExchangeProtocol.Version) throw new InvalidDataException("Protocol version mismatch.");
        var ledger = new AccountRiskLedger(manifest.Assets); ledger.BeginSnapshot(Money(manifest.Fee));
        for (int i = 0; i < manifest.Assets.Length; i++) ledger.SetBalance(i, Money(manifest.InitialFree[i]), 0);
        ledger.CompleteSnapshot();
        HotConfig config = new();
        var rs = manifest.Instruments.Select(i =>
        {
            var instrument = new Instrument(i.Id, i.Symbol, i.Symbol[..^4], "USDT", Money(i.Tick), Money(i.Step), Money(i.Step), 1000000, 0);
            long target = instrument.ToQuantityUnits(100);
            var r = new InstrumentRuntime(instrument, config, ledger, target, checked(target + config.BaseQuantity * 10), estimateBookFromFeed: true); r.OnBalanceChanged(); return r;
        }).ToArray();
        var trader = new SimulationTrader(ledger, rs); var touched = new List<int>();
        var feed = new BinanceFeedReader("", manifest.Instruments.Select(i => i.Symbol).ToArray(), "ws://127.0.0.1", FeedMode.ReplayWithEstimatedBook, false, rs);
        feed.DecisionProduced += (r, _) => touched.Add(r.Instrument.Id);
        var market = new SpscRingBuffer<byte[]>(Capacity); var reports = new SpscRingBuffer<ExchangePacket>(Capacity); var outbound = new SpscRingBuffer<ExchangePacket>(Capacity);
        using var wake = new SemaphoreSlim(0); using var sendWake = new SemaphoreSlim(0);
        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? fault = null; int reportHigh = 0, marketHigh = 0, outHigh = 0;
        void Failed(Exception e) { Interlocked.CompareExchange(ref fault, e, null); wake.Release(); sendWake.Release(); }
        async Task Read(ClientWebSocket socket, bool isMarket)
        {
            var receiveScratch = new byte[16384];
            try
            {
                while (await Receive(socket, ct, receiveScratch) is { } bytes)
                {
                    bool ok = isMarket ? market.TryEnqueue(bytes) : reports.TryEnqueue(Packet(bytes));
                    if (!ok) throw new IOException("Client inbound queue overflow.");
                    if (isMarket) marketHigh = Math.Max(marketHigh, market.Count); else reportHigh = Math.Max(reportHigh, reports.Count); wake.Release();
                }
                if (!ct.IsCancellationRequested) throw new IOException("Exchange disconnected.");
            }
            catch (Exception) when (ct.IsCancellationRequested) { }
            catch (Exception e) { Failed(e); }
        }
        async Task Write()
        { try { while (true) { await sendWake.WaitAsync(ct); while (outbound.TryDequeue(out var p)) { await Send(os, p, ct); if (p.Kind == "settled") flushed.TrySetResult(); } } } catch (Exception) when (ct.IsCancellationRequested) { } catch (Exception e) { Failed(e); } }
        void Queue(ExchangePacket p) { if (!outbound.TryEnqueue(p)) throw new IOException("Client outbound queue overflow."); outHigh = Math.Max(outHigh, outbound.Count); sendWake.Release(); }
        await ms.ConnectAsync(new Uri(url + "/market"), ct);
        Task marketTask = Read(ms, true), reportTask = Read(os, false), sendTask = Write();
        long frames = 0, measuredStart = 0, cpuStart = 0, allocatedStart = 0, lastMarketTick = 0, measuredEnd = 0, cpuEnd = 0, allocatedEnd = 0; int[] gcStart = new int[3], gcEnd = new int[3]; bool finishing = false;
        var processing = new List<long>(); var reportApply = new List<long>(); var sendToApply = new List<long>();
        using var process = Process.GetCurrentProcess(); var wall = Stopwatch.StartNew();
        var readiness = new long[rs.Length, 4];
        try
        {
            void Drain()
            {
                while (reports.TryDequeue(out var p))
                {
                    if (p.Kind != "reports") throw new InvalidDataException("Unexpected exchange response.");
                    foreach (var e in p.Events ?? []) { long t = Stopwatch.GetTimestamp(); trader.Apply(e, finishing); if (measure && !finishing && frames > manifest.Warmup) { reportApply.Add(Stopwatch.GetTimestamp() - t); sendToApply.Add(Stopwatch.GetTimestamp() - e.ServerTicks); } }
                }
                if (!finishing && trader.Outbox.Count > 0) { Queue(new("commands", frames, Commands: trader.Outbox.ToArray())); trader.Outbox.Clear(); }
            }
            byte[]? pendingFrame = null;
            while (true)
            {
                trader.Measure = measure && !finishing && frames >= manifest.Warmup;
                if (fault is not null) throw new IOException("Simulated transport failed.", fault);
                // Deterministic replacement commands belong to the next frame's command batch.
                if (manifest.Mode == "async") Drain();
                if (pendingFrame is null && !market.TryDequeue(out pendingFrame)) { await wake.WaitAsync(ct); continue; }
                if (pendingFrame.Length < 24) throw new InvalidDataException("Short market envelope.");
                long step = BinaryPrimitives.ReadInt64LittleEndian(pendingFrame), watermark = BinaryPrimitives.ReadInt64LittleEndian(pendingFrame.AsSpan(8));
                if (step == 0) { finishing = true; trader.Measure = false; measuredEnd = Stopwatch.GetTimestamp(); process.Refresh(); cpuEnd = process.TotalProcessorTime.Ticks; allocatedEnd = GC.GetTotalAllocatedBytes(false); for (int i = 0; i < 3; i++) gcEnd[i] = GC.CollectionCount(i); }
                if (manifest.Mode == "deterministic" || step == -1)
                {
                    while (reports.TryDequeue(out var p)) foreach (var e in p.Events ?? []) trader.Apply(e, finishing);
                    if (trader.Reports < watermark) { await wake.WaitAsync(ct); continue; }
                }
                if (step == -1) { trader.AssertSettled(); Queue(new("settled")); break; }
                if (step == 0) { finishing = true; trader.Measure = false; trader.Outbox.Clear(); Queue(new("finish")); pendingFrame = null; continue; }
                if (step != frames + 1) throw new InvalidDataException("Market sequence mismatch.");
                long begin = Stopwatch.GetTimestamp(); touched.Clear(); feed.ProcessBinaryFrame(pendingFrame.AsSpan(24));
                long eventTime = checked(BinaryPrimitives.ReadInt64LittleEndian(pendingFrame.AsSpan(32)) * 1000);
                foreach (int index in touched) { trader.LastMarketTime[index] = Math.Max(trader.LastMarketTime[index], eventTime); trader.Decide(index); var r = rs[index]; readiness[index, r.Ready ? 0 : !r.OrderBook.IsSynchronized ? 1 : r.SignalMemory.Faulted ? 3 : 2]++; }
                frames++; lastMarketTick = Stopwatch.GetTimestamp();
                if (measure && frames > manifest.Warmup) processing.Add(lastMarketTick - begin);
                if (frames == manifest.Warmup) { measuredStart = lastMarketTick; process.Refresh(); for (int i = 0; i < 3; i++) gcStart[i] = GC.CollectionCount(i); cpuStart = process.TotalProcessorTime.Ticks; allocatedStart = GC.GetTotalAllocatedBytes(false); }
                if (manifest.Mode == "deterministic" || trader.Outbox.Count > 0) Queue(new("commands", frames, Commands: trader.Outbox.ToArray()));
                trader.Outbox.Clear(); pendingFrame = null;
            }
            // The last protocol acknowledgement must leave the queue before closing the sockets.
            await flushed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            process.Refresh(); long cpu = checked((cpuEnd - cpuStart) * 100), rss = process.WorkingSet64;
            var report = new
            {
                schema_version = 1,
                instrumented = measure,
                language = "CSharp",
                mode = manifest.Mode,
                completed = true,
                frames,
                commands = trader.Commands,
                reports = trader.Reports,
                decisions = trader.Decisions,
                ready_decisions = trader.ReadyDecisions,
                trace_hash = trader.TraceHash(),
                state = trader.Snapshot(),
                cpu_nanoseconds = cpu,
                resident_bytes = rss,
                memory_snapshot_phase = "after_drain_before_report",
                allocated_bytes = allocatedEnd - allocatedStart,
                gc_collections = gcEnd.Zip(gcStart, (a, b) => a - b).ToArray(),
                drain_cpu_nanoseconds = (process.TotalProcessorTime.Ticks - cpuEnd) * 100,
                measured_duration_ns = measuredEnd - measuredStart,
                drain_duration_ns = Stopwatch.GetTimestamp() - measuredEnd,
                wall_seconds = wall.Elapsed.TotalSeconds,
                processing_ns = Summary(processing),
                report_application_ns = Summary(reportApply),
                command_creation_to_response_apply_ns = Summary(trader.ResponseTimes),
                report_delivery_and_apply_ns = Summary(sendToApply),
                queue_high_water = new { market = marketHigh, reports = reportHigh, outbound = outHigh },
                readiness = rs.Select((r, i) => new { symbol = r.Instrument.Symbol, ready = readiness[i, 0], book_unready = readiness[i, 1], warming = readiness[i, 2], signal_fault = readiness[i, 3] }).ToArray()
            };
            if (measure)
            {
                using var csv = new StreamWriter(output + ".samples.csv"); await csv.WriteLineAsync("metric,index,nanoseconds");
                foreach (var (name, samples) in new[] { ("frame_processing", processing), ("report_application", reportApply), ("command_creation_to_response_apply", trader.ResponseTimes), ("report_delivery_and_apply", sendToApply) })
                    for (int i = 0; i < samples.Count; i++) await csv.WriteLineAsync($"{name},{i},{samples[i]}");
            }
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, Json), ct);
        }
        finally { ledger.Invalidate(); await lifetime.CancelAsync(); os.Abort(); ms.Abort(); await Task.WhenAll(marketTask, reportTask, sendTask); }
    }
    private static object Summary(List<long> samples)
    {
        samples = samples.Order().ToList(); long At(double q) => samples.Count == 0 ? 0 : samples[(int)Math.Ceiling(samples.Count * q) - 1];
        return new { count = samples.Count, p50 = At(.5), p99 = At(.99), p999 = At(.999), maximum = samples.Count == 0 ? 0 : samples[^1] };
    }
}
