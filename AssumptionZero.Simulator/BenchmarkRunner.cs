using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AssumptionZero.Infrastructure;
using AssumptionZero.Infrastructure.Benchmarking;

namespace AssumptionZero.Simulator;

public sealed record ThroughputWindow(int Second, int Frames, long Bytes, int Decisions);
public sealed record PerfCounters(string Scope, long? Cycles, long? Instructions, long? CacheMisses,
    long? BranchMisses, long? ContextSwitches, string? UnavailableReason);
public sealed record BenchmarkRunSummary(int SchemaVersion, string Language, string RecordingId,
    string ReplayStateSource, string? FixtureSha256,
    double Speed, long ClockFrequency, string Machine, string OperatingSystem, string Runtime, string BuildConfiguration,
    int LogicalCpus, bool ServerGc, string GcLatencyMode, int WarmupFrames,
    bool Diagnostic, bool Complete, bool ReplayFidelityPassed, bool DurationPassed, bool SampleCountPassed,
    bool P99Passed, bool Passed, string DecisionHash,
    long AchievedFramesPerSecond, long AchievedDecisionsPerSecond,
    int SentFrames, long SentBytes, long ScheduledDurationNanoseconds, long ActualSendDurationNanoseconds,
    LatencySummary SendToDecisionNanoseconds, LatencySummary ReceiveToDecisionNanoseconds,
    LatencySummary ProcessingNanoseconds, LatencySummary SendToProcessingNanoseconds,
    TimingStatistics ReplaySendLatenessNanoseconds, TimingStatistics ReplaySendDurationNanoseconds,
    BenchmarkAppOverview Application, PerfCounters HardwareCounters,
    ThroughputWindow[] PerSecond, double ProcessWallSeconds, string? Error);
public sealed record SpeedSummary(double Speed, bool Passed, string? Failure,
    double MeasurementWallOverheadPercent, BenchmarkRunSummary[] Runs);
public sealed record SweepSummary(int SchemaVersion, string RecordingId, double? HighestPassingSpeed,
    SpeedSummary[] Speeds);

public static class BenchmarkAnalysis
{
    public static (LatencySummary SendToDecision, LatencySummary SendToProcessing) Summarize(
        FrameSample[] samples, long[] sendStarts, int warmup, int sent, long frequency)
    {
        var sendToDecision = new long[Math.Max(0, samples.Length - warmup)];
        var sendToProcessing = new long[Math.Max(0, samples.Length - warmup)];
        int decisionFrames = 0, completedFrames = 0;
        for (int i = warmup; i < samples.Length && i < sent; i++)
        {
            long processingDelta = samples[i].ProcessingEndTicks - sendStarts[i];
            if (processingDelta < 0) throw new InvalidDataException("Cross-process monotonic timestamps are not aligned.");
            sendToProcessing[completedFrames++] = NanosecondTime.FromTicks(processingDelta, frequency);
            if (samples[i].LastDecisionTicks == 0) continue;
            long decisionDelta = samples[i].LastDecisionTicks - sendStarts[i];
            if (decisionDelta < 0) throw new InvalidDataException("Cross-process monotonic timestamps are not aligned.");
            sendToDecision[decisionFrames++] = NanosecondTime.FromTicks(decisionDelta, frequency);
        }
        return (LatencySummary.From(sendToDecision, decisionFrames),
            LatencySummary.From(sendToProcessing, completedFrames));
    }
}

public static class BenchmarkRunner
{
    private sealed record Options(bool Sweep, string Recording, string? Fixture, string Output,
        double Speed, double MaxSpeed, long P99LimitNs, long ReplayLatenessLimitNs, bool Diagnostic);

    public static async Task<int> RunAsync(string[] args)
    {
        Options options = Parse(args);
        Directory.CreateDirectory(options.Output);
        string? fixture = options.Fixture is null ? null : Path.GetFullPath(options.Fixture);
        if (fixture is not null && !File.Exists(fixture))
            throw new FileNotFoundException("Replay fixture was not found.", fixture);
        string? fixtureHash = fixture is null ? null :
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fixture))).ToLowerInvariant();
        Recording recording;
        if (options.Recording == "demo") recording = DemoRecording.Create();
        else
        {
            string connection = Environment.GetEnvironmentVariable("PG_CONNECTION") ??
                "Host=172.17.0.2;Database=assumption_zero;Username=postgres;Password=mysecretpassword;Trust Server Certificate=true;";
            recording = await new RecordingRepository(connection).LoadAsync(Guid.Parse(options.Recording));
        }
        string appPath = FindApp();
        string[] perfEvents = await ProbePerf();
        var results = new List<SpeedSummary>();
        double? highest = null;
        string? expectedHash = null;
        async Task<SpeedSummary> Measure(double speed)
        {
            SpeedSummary result;
            try
            {
                result = await MeasureSpeed(recording, options, speed, fixture, fixtureHash, appPath, perfEvents);
                string? hash = result.Runs.FirstOrDefault()?.DecisionHash;
                if (hash is not null)
                {
                    expectedHash ??= hash;
                    if (hash != expectedHash)
                        result = result with { Passed = false, Failure = "decision hash differs from first measured speed" };
                }
            }
            catch (Exception exception)
            {
                result = new SpeedSummary(speed, false, exception.Message, 0, []);
            }
            results.Add(result);
            if (result.Passed && (!highest.HasValue || speed > highest)) highest = speed;
            Console.WriteLine($"{speed:0.###}x: {(options.Diagnostic ? "DIAGNOSTIC" : result.Passed ? "PASS" : "FAIL")} {result.Failure}");
            return result;
        }
        if (!options.Sweep) await Measure(options.Speed);
        else
        {
            double low = 0, high = 0, speed = 1;
            while (speed <= options.MaxSpeed)
            {
                SpeedSummary result = await Measure(speed);
                if (!result.Passed) { high = speed; break; }
                low = speed;
                if (speed == options.MaxSpeed) break;
                speed = Math.Min(options.MaxSpeed, speed * 2);
            }
            if (low > 0 && high > 0)
                for (int i = 0; i < 8 && (high - low) / low > .1; i++)
                {
                    double mid = (low + high) / 2;
                    if ((await Measure(mid)).Passed) low = mid; else high = mid;
                }
        }
        var summary = new SweepSummary(4, recording.Id.ToString(), highest, results.ToArray());
        await File.WriteAllTextAsync(Path.Combine(options.Output, "comparison.json"),
            JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        return options.Diagnostic || highest.HasValue ? 0 : 2;
    }

    private static Options Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("run" or "sweep"))
            throw new ArgumentException("benchmark run|sweep --recording UUID|demo --out DIR " +
                "--p99-limit-us N --replay-p99-lateness-us N [--fixture PATH] " +
                "[--speed N|--max-speed N] [--diagnostic]");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--diagnostic") { map[args[i]] = "1"; continue; }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
                throw new ArgumentException($"Invalid option {args[i]}.");
            map[args[i]] = args[++i];
        }
        string Required(string key) => map.TryGetValue(key, out string? value) ? value :
            throw new ArgumentException($"{key} is required.");
        double Number(string key, double fallback) => map.TryGetValue(key, out string? value) ?
            double.Parse(value, CultureInfo.InvariantCulture) : fallback;
        double speed = Number("--speed", 1), maxSpeed = Number("--max-speed", 16);
        double p99Us = double.Parse(Required("--p99-limit-us"), CultureInfo.InvariantCulture);
        double replayUs = double.Parse(Required("--replay-p99-lateness-us"), CultureInfo.InvariantCulture);
        if (!double.IsFinite(speed) || !double.IsFinite(maxSpeed) || speed <= 0 || speed > 1_000 ||
            maxSpeed < 1 || maxSpeed > 1_000 ||
            !double.IsFinite(p99Us) || !double.IsFinite(replayUs) || p99Us <= 0 || replayUs <= 0)
            throw new ArgumentOutOfRangeException(nameof(args), "Speeds and latency limits must be positive and finite.");
        return new(args[0] == "sweep", Required("--recording"),
            map.TryGetValue("--fixture", out string? fixture) ? fixture : null,
            Path.GetFullPath(Required("--out")), speed, maxSpeed,
            checked((long)Math.Ceiling(p99Us * 1_000)), checked((long)Math.Ceiling(replayUs * 1_000)),
            map.ContainsKey("--diagnostic"));
    }

    private static string FindApp()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        string path = Path.Combine(root, "AssumptionZero.App/bin/Release/net10.0/AssumptionZero.App.dll");
        if (!File.Exists(path)) throw new FileNotFoundException("Build the solution in Release first.", path);
        return path;
    }

    private static async Task<SpeedSummary> MeasureSpeed(Recording recording, Options options, double speed,
        string? fixture, string? fixtureHash, string appPath, string[] perfEvents)
    {
        string directory = Path.Combine(options.Output, $"speed-{speed.ToString("0.###", CultureInfo.InvariantCulture)}");
        Directory.CreateDirectory(directory);
        OnceResult baseline = await RunOnce(recording, speed, fixture, appPath, null, 0, false, []);
        if (baseline.ExitCode != 0 || baseline.Replay?.Completed != true)
            throw new InvalidOperationException($"Uninstrumented baseline failed: {baseline.Error}");
        double baselineWall = baseline.WallSeconds;
        var runs = new List<BenchmarkRunSummary>();
        int warmup = Math.Min(recording.Messages.Count - 1, (int)Math.Ceiling(recording.Messages.Count * .1));
        for (int i = 1; i <= 5; i++)
        {
            string prefix = Path.Combine(directory, $"run-{i}");
            var result = await RunOnce(recording, speed, fixture, appPath, prefix, warmup,
                options.Diagnostic, perfEvents);
            if (result.App is null || result.Replay is null)
                throw new InvalidOperationException($"Benchmark run {i} did not produce reports: {result.Error}");
            var app = result.App;
            var replay = result.Replay;
            if (app.SchemaVersion != 3 || app.DecodeNanoseconds is null || app.BookNanoseconds is null ||
                app.SignalNanoseconds is null || app.QuoteNanoseconds is null)
                throw new InvalidDataException("The app benchmark report version does not match the runner.");
            if (app.ClockFrequency != Stopwatch.Frequency)
                throw new InvalidDataException("Simulator and app have different monotonic clock frequencies.");
            (LatencySummary sendLatency, LatencySummary allFrameLatency) = BenchmarkAnalysis.Summarize(
                app.Frames, replay.SendStartTicks, warmup, replay.SentMessages, Stopwatch.Frequency);
            bool complete = result.ExitCode == 0 && app.Completed && replay.Completed &&
                app.ReceivedFrames == recording.Messages.Count && replay.SentMessages == recording.Messages.Count;
            bool fidelity = replay.SendStartLateness.P99 <= options.ReplayLatenessLimitNs;
            bool duration = app.MeasuredDurationNanoseconds >= 30_000_000_000L;
            bool sampleCount = app.MeasuredFrames >= 10_000;
            bool p99 = sendLatency.Count > 0 && sendLatency.P99 <= options.P99LimitNs &&
                allFrameLatency.P99 <= options.P99LimitNs;
            bool passed = complete && fidelity && duration && sampleCount && p99 && !options.Diagnostic;
            long achieved = app.MeasuredDurationNanoseconds == 0 ? 0 :
                (long)(app.MeasuredFrames * 1_000_000_000.0 / app.MeasuredDurationNanoseconds);
            long decisionsPerSecond = app.MeasuredDurationNanoseconds == 0 ? 0 :
                (long)(app.MeasuredDecisions * 1_000_000_000.0 / app.MeasuredDurationNanoseconds);
            string? error = complete ? null : $"exit={result.ExitCode}; sent={replay.SentMessages}; received={app.ReceivedFrames}; {result.Error}";
            var summary = new BenchmarkRunSummary(4, "CSharp", recording.Id.ToString(),
                fixture is null ? "synthetic" : "fixture", fixtureHash,
                speed, app.ClockFrequency, Environment.MachineName, RuntimeInformation.OSDescription,
                RuntimeInformation.FrameworkDescription, "Release", Environment.ProcessorCount,
                GCSettings.IsServerGC, GCSettings.LatencyMode.ToString(), warmup,
                options.Diagnostic, complete, fidelity, duration, sampleCount, p99, passed,
                app.DecisionHash, achieved, decisionsPerSecond, replay.SentMessages, replay.SentBytes,
                replay.ExpectedDurationNanoseconds, replay.ActualDurationNanoseconds,
                sendLatency, app.ReceiveToDecisionNanoseconds, app.ProcessingNanoseconds,
                allFrameLatency, replay.SendStartLateness, replay.SendDuration,
                BenchmarkAppOverview.From(app), result.Perf,
                Windows(app, warmup), result.WallSeconds, error);
            runs.Add(summary);
            await File.WriteAllTextAsync(prefix + "-summary.json", JsonSerializer.Serialize(summary,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        bool sameDecisions = runs.Select(r => r.DecisionHash).Distinct().Count() == 1;
        bool allPassed = runs.All(r => r.Passed) && sameDecisions;
        double overhead = baselineWall <= 0 ? 0 : (runs.Average(r => r.ProcessWallSeconds) / baselineWall - 1) * 100;
        string? failure = allPassed ? null : !sameDecisions ? "decision hashes differ" :
            string.Join(", ", new[]
            {
                runs.Any(r => !r.Complete) ? "incomplete" : null,
                runs.Any(r => !r.ReplayFidelityPassed) ? "replay lateness" : null,
                runs.Any(r => !r.DurationPassed) ? "under 30 s" : null,
                runs.Any(r => !r.SampleCountPassed) ? "under 10000 frames" : null,
                runs.Any(r => !r.P99Passed) ? "P99 limit" : null,
                options.Diagnostic ? "diagnostic runs cannot set capacity" : null
            }.Where(x => x is not null));
        return new SpeedSummary(speed, allPassed, failure, overhead, runs.ToArray());
    }

    private static ThroughputWindow[] Windows(BenchmarkAppReport app, int warmup)
    {
        if (app.MeasuredFrames == 0) return [];
        long origin = app.Frames[warmup].ReceiveTicks;
        var windows = new List<ThroughputWindow>();
        for (int i = warmup; i < app.Frames.Length; i++)
        {
            FrameSample frame = app.Frames[i];
            int second = (int)((frame.ReceiveTicks - origin) / app.ClockFrequency);
            while (windows.Count <= second) windows.Add(new(windows.Count, 0, 0, 0));
            ThroughputWindow old = windows[second];
            windows[second] = old with { Frames = old.Frames + 1, Bytes = old.Bytes + frame.Bytes,
                Decisions = old.Decisions + frame.Decisions };
        }
        return windows.ToArray();
    }

    private sealed record OnceResult(BenchmarkAppReport? App, ReplayReport? Replay, PerfCounters Perf,
        double WallSeconds, int ExitCode, string? Error);

    private static async Task<OnceResult> RunOnce(Recording recording, double speed, string? fixture,
        string appPath, string? prefix, int warmup, bool diagnostic, string[] perfEvents)
    {
        var reportSource = new TaskCompletionSource<ReplayReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = ReplayServer.Create(recording, "http://127.0.0.1:0",
            report => reportSource.TrySetResult(report), speed: speed);
        await server.StartAsync();
        string url = server.Urls.Single().Replace("http://", "ws://") + "/stream";
        string dotnet = Environment.ProcessPath ?? "dotnet";
        string? perfPath = prefix is null ? null : prefix + "-perf.csv";
        bool usePerf = perfEvents.Length > 0;
        using var process = new Process();
        var start = process.StartInfo;
        start.FileName = usePerf ? "perf" : dotnet;
        if (usePerf)
        {
            foreach (string arg in new[] { "stat", "-x,", "-e",
                         string.Join(',', perfEvents), "-o", perfPath!, "--", dotnet })
                start.ArgumentList.Add(arg);
        }
        start.ArgumentList.Add(appPath);
        start.Environment["EXECUTION_MODE"] = "Replay";
        if (fixture is null) start.Environment.Remove("REPLAY_FIXTURE");
        else start.Environment["REPLAY_FIXTURE"] = fixture;
        start.Environment["REPLAY_URL"] = url;
        start.Environment["LOG_EVENTS"] = "false";
        if (prefix is not null)
        {
            start.Environment["BENCHMARK_OUTPUT"] = prefix + "-samples.json";
            start.Environment["BENCHMARK_EXPECTED_FRAMES"] = recording.Messages.Count.ToString(CultureInfo.InvariantCulture);
            start.Environment["BENCHMARK_WARMUP_FRAMES"] = warmup.ToString(CultureInfo.InvariantCulture);
            if (diagnostic) start.Environment["BENCHMARK_DIAGNOSTIC"] = "1";
        }
        start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.UseShellExecute = false;
        var wall = Stopwatch.StartNew();
        process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(
            Math.Max(60, recording.DurationNanoseconds / speed / 1e9 * 3 + 30)));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        wall.Stop();
        string output = await stdout, error = await stderr;
        if (prefix is not null)
        {
            await File.WriteAllTextAsync(prefix + "-stdout.txt", output);
            await File.WriteAllTextAsync(prefix + "-stderr.txt", error);
        }
        ReplayReport? replay = null;
        try { replay = await reportSource.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { }
        BenchmarkAppReport? app = null;
        if (prefix is not null && File.Exists(prefix + "-samples.json"))
        {
            app = JsonSerializer.Deserialize<BenchmarkAppReport>(await File.ReadAllTextAsync(prefix + "-samples.json"));
            if (app is not null)
            {
                await WriteSamplesCsv(prefix + "-samples.csv", app.Frames, replay?.SendStartTicks);
                await File.WriteAllTextAsync(prefix + "-gc-pauses.json", JsonSerializer.Serialize(app.GcPauses));
                File.Delete(prefix + "-samples.json");
            }
        }
        var perf = usePerf && perfPath is not null ? ParsePerf(perfPath) :
            new PerfCounters("whole_process", null, null, null, null, null,
                usePerf ? "perf report unavailable" : "perf hardware counters unavailable or disabled");
        return new(app, replay, perf, wall.Elapsed.TotalSeconds, process.ExitCode, error);
    }

    private static async Task<string[]> ProbePerf()
    {
        if (!OperatingSystem.IsLinux()) return [];
        var supported = new List<string>();
        foreach (string eventName in new[] { "cycles", "instructions", "cache-misses",
                     "branch-misses", "context-switches" })
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("perf")
                {
                    ArgumentList = { "stat", "-e", eventName, "--", "true" },
                    RedirectStandardError = true, UseShellExecute = false
                });
                if (process is null) continue;
                string output = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                if (process.ExitCode == 0 && !output.Contains("not supported", StringComparison.OrdinalIgnoreCase))
                    supported.Add(eventName);
            }
            catch { /* This counter is unavailable on this host. */ }
        }
        return supported.ToArray();
    }

    private static PerfCounters ParsePerf(string path)
    {
        if (!File.Exists(path)) return new("whole_process", null, null, null, null, null, "perf output missing");
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(path))
        {
            string[] fields = line.Split(',');
            if (fields.Length < 3) continue;
            if (long.TryParse(fields[0].Trim(), NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out long value))
                values[fields[2].Trim()] = value;
        }
        long? Get(string key) => values.TryGetValue(key, out long value) ? value : null;
        string[] missing = new[] { "cycles", "instructions", "cache-misses", "branch-misses",
            "context-switches" }.Where(name => !values.ContainsKey(name)).ToArray();
        return new("whole_process", Get("cycles"), Get("instructions"), Get("cache-misses"),
            Get("branch-misses"), Get("context-switches"), missing.Length == 0 ? null :
                "unavailable: " + string.Join(", ", missing));
    }

    private static async Task WriteSamplesCsv(string path, FrameSample[] frames, long[]? sendStartTicks)
    {
        await using var stream = new StreamWriter(path);
        await stream.WriteLineAsync("index,type,bytes,template,send_ticks,receive_ticks,end_ticks,last_decision_ticks,decisions,decode_ticks,book_ticks,signal_ticks,quote_ticks,gc_overlap");
        for (int i = 0; i < frames.Length; i++)
        {
            FrameSample f = frames[i];
            long sendTicks = sendStartTicks is not null && i < sendStartTicks.Length ? sendStartTicks[i] : 0;
            await stream.WriteLineAsync(FormattableString.Invariant(
                $"{f.Index},{(int)f.MessageType},{f.Bytes},{f.TemplateId},{sendTicks},{f.ReceiveTicks},{f.ProcessingEndTicks},{f.LastDecisionTicks},{f.Decisions},{f.DecodeTicks},{f.BookTicks},{f.SignalTicks},{f.QuoteTicks},{(f.GcPauseOverlap ? 1 : 0)}"));
        }
    }
}
