using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using AssumptionZero.Application.Interfaces.External;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.Entities;
using AssumptionZero.Domain.InMemory;
using AssumptionZero.Infrastructure;
using AssumptionZero.Infrastructure.External;
using AssumptionZero.Infrastructure.Simulation;
using static AssumptionZero.Infrastructure.Simulation.ExchangeProtocol;

namespace AssumptionZero.App;

/// <summary>Owns one connection's queues, trading state, measurements, and shutdown sequence.</summary>
internal sealed class ClosedLoopClientSession
{
    private bool measure;
    private CancellationToken cancellationToken;
    private ClientWebSocket orderSocket = null!;
    private ClientWebSocket marketSocket = null!;
    private SimManifest manifest = null!;
    private AccountRiskLedger ledger = null!;
    private InstrumentRuntime[] runtimes = null!;
    private SimulationTrader trader = null!;
    private HotConfig config;
    private IDataFeedReader feed = null!;
    private List<int> touchedInstruments = null!;
    private SpscRingBuffer<byte[]> marketFrames = null!;
    private SpscRingBuffer<ExchangePacket> reports = null!;
    private SpscRingBuffer<ExchangePacket> outbound = null!;
    private SemaphoreSlim wake = null!;
    private SemaphoreSlim sendWake = null!;
    private TaskCompletionSource flushed = null!;
    private Exception? fault;
    private int reportHighWater, marketHighWater, outboundHighWater;
    private long frames, measuredStart, cpuStart, allocatedStart, measuredEnd, cpuEnd, allocatedEnd;
    private int[] gcStart = null!, gcEnd = null!;
    private bool finishing;
    private List<long> processing = null!, reportApply = null!, sendToApply = null!;
    private Process process = null!;
    private Stopwatch wall = null!;
    private long[,] readiness = null!;

    public async Task Run(string url, string output, CancellationToken stop)
    {
        measure = Environment.GetEnvironmentVariable("SIM_MEASURE") != "false";
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        cancellationToken = lifetime.Token;
        using var orders = orderSocket = new ClientWebSocket();
        using var market = marketSocket = new ClientWebSocket();
        await orderSocket.ConnectAsync(new Uri(url + "/orders"), cancellationToken);
        ReadManifest(await Receive(orderSocket, cancellationToken));
        InitializeTradingState();
        InitializeQueues();
        using var sessionWake = wake = new SemaphoreSlim(0);
        using var writerWake = sendWake = new SemaphoreSlim(0);
        flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await marketSocket.ConnectAsync(new Uri(url + "/market"), cancellationToken);
        Task marketTask = ReadMessages(marketSocket, true);
        Task reportTask = ReadMessages(orderSocket, false);
        Task sendTask = WriteMessages();
        InitializeMeasurements();
        using var sessionProcess = process = Process.GetCurrentProcess();
        wall = Stopwatch.StartNew();
        readiness = new long[runtimes.Length, 4];
        try
        {
            await ProcessSession();
            // The final acknowledgement must leave the queue before the sockets close.
            await flushed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            await WriteReport(output);
        }
        finally
        {
            ledger.Invalidate();
            await lifetime.CancelAsync();
            orderSocket.Abort();
            marketSocket.Abort();
            await Task.WhenAll(marketTask, reportTask, sendTask);
        }
    }

    private void ReadManifest(byte[]? bytes)
    {
        ExchangePacket hello = Packet(bytes ?? throw new IOException("No exchange manifest."));
        if (hello.Kind != "hello")
        {
            throw new InvalidDataException("Expected manifest handshake.");
        }

        manifest = hello.Manifest ?? throw new InvalidDataException("Manifest missing.");
        if (manifest.Version != ExchangeProtocol.Version)
        {
            throw new InvalidDataException("Protocol version mismatch.");
        }
    }

    private void InitializeTradingState()
    {
        ledger = new AccountRiskLedger(manifest.Assets);
        ledger.BeginSnapshot(Money(manifest.Fee));
        for (int i = 0; i < manifest.Assets.Length; i++)
        {
            ledger.SetBalance(i, Money(manifest.InitialFree[i]), 0);
        }

        ledger.CompleteSnapshot();
        config = new HotConfig();
        runtimes = manifest.Instruments.Select(CreateRuntime).ToArray();
        trader = new SimulationTrader(ledger, runtimes);
        touchedInstruments = new List<int>();
        var reader = new BinanceFeedReader(
            "",
            manifest.Instruments.Select(i => i.Symbol).ToArray(),
            "ws://127.0.0.1",
            FeedMode.ReplayWithEstimatedBook,
            false,
            runtimes);
        reader.DecisionProduced += RecordTouchedInstrument;
        feed = reader;
    }

    private InstrumentRuntime CreateRuntime(SimInstrument specification)
    {
        var instrument = new Instrument(
            specification.Id,
            specification.Symbol,
            specification.Symbol[..^4],
            "USDT",
            Money(specification.Tick),
            Money(specification.Step),
            Money(specification.Step),
            1000000,
            0);
        long target = instrument.ToQuantityUnits(100);
        var runtime = new InstrumentRuntime(
            instrument,
            config,
            ledger,
            target,
            checked(target + config.BaseQuantity * 10),
            estimateBookFromFeed: true);
        runtime.OnBalanceChanged();
        return runtime;
    }

    private void RecordTouchedInstrument(InstrumentRuntime runtime, QuoteDecision decision)
    {
        touchedInstruments.Add(runtime.Instrument.Id);
    }

    private void InitializeQueues()
    {
        marketFrames = new SpscRingBuffer<byte[]>(Capacity);
        reports = new SpscRingBuffer<ExchangePacket>(Capacity);
        outbound = new SpscRingBuffer<ExchangePacket>(Capacity);
    }

    private void InitializeMeasurements()
    {
        gcStart = new int[3];
        gcEnd = new int[3];
        processing = new List<long>();
        reportApply = new List<long>();
        sendToApply = new List<long>();
    }

    private void RecordTransportFailure(Exception exception)
    {
        Interlocked.CompareExchange(ref fault, exception, null);
        wake.Release();
        sendWake.Release();
    }

    private async Task ReadMessages(ClientWebSocket socket, bool isMarket)
    {
        var receiveScratch = new byte[16384];
        try
        {
            while (await Receive(socket, cancellationToken, receiveScratch) is { } bytes)
            {
                bool queued = isMarket ? marketFrames.TryEnqueue(bytes) : reports.TryEnqueue(Packet(bytes));
                if (!queued)
                {
                    throw new IOException("Client inbound queue overflow.");
                }

                if (isMarket)
                {
                    marketHighWater = Math.Max(marketHighWater, marketFrames.Count);
                }
                else
                {
                    reportHighWater = Math.Max(reportHighWater, reports.Count);
                }

                wake.Release();
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("Exchange disconnected.");
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RecordTransportFailure(exception);
        }
    }

    private async Task WriteMessages()
    {
        try
        {
            while (true)
            {
                await sendWake.WaitAsync(cancellationToken);
                while (outbound.TryDequeue(out ExchangePacket packet))
                {
                    await Send(orderSocket, packet, cancellationToken);
                    if (packet.Kind == "settled")
                    {
                        flushed.TrySetResult();
                    }
                }
            }
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            RecordTransportFailure(exception);
        }
    }

    private void QueuePacket(ExchangePacket packet)
    {
        if (!outbound.TryEnqueue(packet))
        {
            throw new IOException("Client outbound queue overflow.");
        }

        outboundHighWater = Math.Max(outboundHighWater, outbound.Count);
        sendWake.Release();
    }

    private void DrainAsyncReports()
    {
        while (reports.TryDequeue(out ExchangePacket packet))
        {
            if (packet.Kind != "reports")
            {
                throw new InvalidDataException("Unexpected exchange response.");
            }

            foreach (ExecutionEvent execution in packet.Events ?? [])
            {
                long begin = Stopwatch.GetTimestamp();
                trader.Apply(execution, finishing);
                if (measure && !finishing && frames > manifest.Warmup)
                {
                    reportApply.Add(Stopwatch.GetTimestamp() - begin);
                    sendToApply.Add(Stopwatch.GetTimestamp() - execution.ServerTicks);
                }
            }
        }

        if (!finishing && trader.Outbox.Count > 0)
        {
            QueuePacket(new("commands", frames, Commands: trader.Outbox.ToArray()));
            trader.Outbox.Clear();
        }
    }

    private async Task ProcessSession()
    {
        byte[]? pendingFrame = null;
        while (true)
        {
            trader.Measure = measure && !finishing && frames >= manifest.Warmup;
            if (fault is not null)
            {
                throw new IOException("Simulated transport failed.", fault);
            }

            // Deterministic replacement commands belong to the next frame's command batch.
            if (manifest.Mode == "async")
            {
                DrainAsyncReports();
            }

            if (pendingFrame is null && !marketFrames.TryDequeue(out pendingFrame))
            {
                await wake.WaitAsync(cancellationToken);
                continue;
            }

            ReadEnvelope(pendingFrame, out long step, out long watermark);
            if (step == 0)
            {
                CaptureMeasurementEnd();
            }

            if ((manifest.Mode == "deterministic" || step == -1) && !ApplyReportsThrough(watermark))
            {
                await wake.WaitAsync(cancellationToken);
                continue;
            }

            if (ProcessReadyFrame(pendingFrame, step))
            {
                break;
            }

            pendingFrame = null;
        }
    }

    private bool ProcessReadyFrame(byte[] frame, long step)
    {
        if (step == -1)
        {
            trader.AssertSettled();
            QueuePacket(new("settled"));
            return true;
        }

        if (step == 0)
        {
            BeginSettlement();
        }
        else
        {
            ProcessMarketFrame(frame, step);
        }

        return false;
    }

    private static void ReadEnvelope(byte[] frame, out long step, out long watermark)
    {
        if (frame.Length < 24)
        {
            throw new InvalidDataException("Short market envelope.");
        }

        step = BinaryPrimitives.ReadInt64LittleEndian(frame);
        watermark = BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(8));
    }

    private bool ApplyReportsThrough(long watermark)
    {
        while (reports.TryDequeue(out ExchangePacket packet))
        {
            foreach (ExecutionEvent execution in packet.Events ?? [])
            {
                trader.Apply(execution, finishing);
            }
        }

        return trader.Reports >= watermark;
    }

    private void BeginSettlement()
    {
        finishing = true;
        trader.Measure = false;
        trader.Outbox.Clear();
        QueuePacket(new("finish"));
    }

    private void ProcessMarketFrame(byte[] frame, long step)
    {
        if (step != frames + 1)
        {
            throw new InvalidDataException("Market sequence mismatch.");
        }

        long begin = Stopwatch.GetTimestamp();
        touchedInstruments.Clear();
        feed.ProcessBinaryFrame(frame.AsSpan(24));
        long eventTime = checked(BinaryPrimitives.ReadInt64LittleEndian(frame.AsSpan(32)) * 1000);
        foreach (int index in touchedInstruments)
        {
            trader.LastMarketTime[index] = Math.Max(trader.LastMarketTime[index], eventTime);
            trader.Decide(index);
            InstrumentRuntime runtime = runtimes[index];
            int state = runtime.Ready
                ? 0
                : !runtime.OrderBook.IsSynchronized ? 1 : runtime.SignalMemory.Faulted ? 3 : 2;
            readiness[index, state]++;
        }

        frames++;
        long lastMarketTick = Stopwatch.GetTimestamp();
        if (measure && frames > manifest.Warmup)
        {
            processing.Add(lastMarketTick - begin);
        }

        if (frames == manifest.Warmup)
        {
            CaptureMeasurementStart(lastMarketTick);
        }

        if (manifest.Mode == "deterministic" || trader.Outbox.Count > 0)
        {
            QueuePacket(new("commands", frames, Commands: trader.Outbox.ToArray()));
        }

        trader.Outbox.Clear();
    }

    private void CaptureMeasurementStart(long lastMarketTick)
    {
        measuredStart = lastMarketTick;
        process.Refresh();
        for (int i = 0; i < 3; i++)
        {
            gcStart[i] = GC.CollectionCount(i);
        }

        cpuStart = process.TotalProcessorTime.Ticks;
        allocatedStart = GC.GetTotalAllocatedBytes(false);
    }

    private void CaptureMeasurementEnd()
    {
        finishing = true;
        trader.Measure = false;
        measuredEnd = Stopwatch.GetTimestamp();
        process.Refresh();
        cpuEnd = process.TotalProcessorTime.Ticks;
        allocatedEnd = GC.GetTotalAllocatedBytes(false);
        for (int i = 0; i < 3; i++)
        {
            gcEnd[i] = GC.CollectionCount(i);
        }
    }

    private async Task WriteReport(string output)
    {
        process.Refresh();
        long cpu = checked((cpuEnd - cpuStart) * 100);
        long rss = process.WorkingSet64;
        object report = CreateReport(cpu, rss);
        if (measure)
        {
            await WriteSamples(output);
        }

        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, Json), cancellationToken);
    }

    // Keep the wire schema together and preserve the order of the measurement reads.

    private object CreateReport(long cpu, long rss) => new
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
        gc_collections = gcEnd.Zip(gcStart, (end, start) => end - start).ToArray(),
        drain_cpu_nanoseconds = (process.TotalProcessorTime.Ticks - cpuEnd) * 100,
        measured_duration_ns = measuredEnd - measuredStart,
        drain_duration_ns = Stopwatch.GetTimestamp() - measuredEnd,
        wall_seconds = wall.Elapsed.TotalSeconds,
        processing_ns = Summary(processing),
        report_application_ns = Summary(reportApply),
        command_creation_to_response_apply_ns = Summary(trader.ResponseTimes),
        report_delivery_and_apply_ns = Summary(sendToApply),
        queue_high_water = new
        {
            market = marketHighWater,
            reports = reportHighWater,
            outbound = outboundHighWater
        },
        readiness = runtimes.Select((runtime, index) => new { symbol = runtime.Instrument.Symbol,
            ready = readiness[index, 0], book_unready = readiness[index, 1], warming = readiness[index, 2],
            signal_fault = readiness[index, 3] }).ToArray()
    };

    private async Task WriteSamples(string output)
    {
        using var csv = new StreamWriter(output + ".samples.csv");
        await csv.WriteLineAsync("metric,index,nanoseconds");
        foreach (var (name, samples)in new[]
        {
            ("frame_processing", processing),
            ("report_application", reportApply),
            ("command_creation_to_response_apply", trader.ResponseTimes),
            ("report_delivery_and_apply", sendToApply)
        }

        )
        {
            for (int i = 0; i < samples.Count; i++)
            {
                await csv.WriteLineAsync($"{name},{i},{samples[i]}");
            }
        }
    }

    private static object Summary(List<long> samples)
    {
        samples = samples.Order().ToList();
        return new
        {
            count = samples.Count,
            p50 = Percentile(samples, .5),
            p99 = Percentile(samples, .99),
            p999 = Percentile(samples, .999),
            maximum = samples.Count == 0 ? 0 : samples[^1]
        };
    }

    private static long Percentile(List<long> sortedSamples, double quantile) =>
        sortedSamples.Count == 0 ? 0 : sortedSamples[(int)Math.Ceiling(sortedSamples.Count * quantile) - 1];
}
