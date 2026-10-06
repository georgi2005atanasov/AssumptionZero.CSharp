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
    int LogicalCpus, bool? ServerGc, string? GcLatencyMode, int WarmupFrames,
    bool Diagnostic, bool Complete, bool ReplayFidelityPassed, bool DurationPassed, bool SampleCountPassed,
    bool P99Passed, bool Passed, string DecisionHash,
    long AchievedFramesPerSecond, long AchievedDecisionsPerSecond,
    int SentFrames, long SentBytes, long ScheduledDurationNanoseconds, long ActualSendDurationNanoseconds,
    LatencySummary SendToDecisionNanoseconds, LatencySummary ReceiveToDecisionNanoseconds,
    LatencySummary ProcessingNanoseconds, LatencySummary SendToProcessingNanoseconds,
    TimingStatistics ReplaySendLatenessNanoseconds, TimingStatistics ReplaySendDurationNanoseconds,
    BenchmarkAppOverview Application, PerfCounters HardwareCounters,
    ThroughputWindow[] PerSecond, double ProcessWallSeconds, string? Error)
{
    public string RunKind { get; init; } = "measurement";
    public bool? CapacityPassed { get; init; }
    public string? FinalStateHash { get; init; }
    public string DatasetSha256 { get; init; } = "";
    public int? RequestedMaxMessages { get; init; }
    public long P99LimitNanoseconds { get; init; }
    public long ReplayLatenessLimitNanoseconds { get; init; }
    public object? Build { get; init; }
    public object? Host { get; init; }
}
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
    public sealed record Options(string Command, string Recording, string? Fixture, string Output,
        double? Speed, double MaxSpeed, double TargetSeconds, long P99LimitNs, long ReplayLatenessLimitNs,
        bool Diagnostic, bool Smoke, bool Trace, int? MaxMessages, string Language, string? RustApp, string? CSharpApp,
        string? AppCpus, int FragmentSize);
    private sealed record Target(string Language, string Executable, string[] Arguments, string Binary, string Runtime, object Build);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static Options Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("run" or "sweep" or "compare"))
            throw new ArgumentException("benchmark run|sweep|compare --recording UUID|demo --out DIR [--language csharp|rust] " +
                "[--rust-app PATH] [--max-messages N|--all] [--speed N|--target-duration-seconds N] [--smoke] [--trace]");
        string[] flags = ["--all", "--smoke", "--diagnostic", "--trace"];
        string[] values = ["--recording", "--out", "--fixture", "--speed", "--max-speed", "--target-duration-seconds",
            "--p99-limit-us", "--replay-p99-lateness-us", "--max-messages", "--language", "--rust-app", "--csharp-app", "--app-cpus", "--fragment-size"];
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            string key = args[i];
            if (map.ContainsKey(key)) throw new ArgumentException($"Duplicate option {key}.");
            if (flags.Contains(key)) map.Add(key, "1");
            else if (values.Contains(key) && i + 1 < args.Length) map.Add(key, args[++i]);
            else throw new ArgumentException($"Unknown option or missing value: {key}.");
        }
        string Required(string key) => map.GetValueOrDefault(key) ?? throw new ArgumentException($"{key} is required.");
        double Number(string key, double fallback) => map.TryGetValue(key, out var value) ? double.Parse(value, CultureInfo.InvariantCulture) : fallback;
        bool smoke = map.ContainsKey("--smoke");
        double? speed = map.ContainsKey("--speed") ? Number("--speed", 1) : smoke && !map.ContainsKey("--target-duration-seconds") ? 1000 : null;
        double max = Number("--max-speed", 16), target = Number("--target-duration-seconds", 60);
        double p99 = Number("--p99-limit-us", 1000), late = Number("--replay-p99-lateness-us", 100);
        if (map.ContainsKey("--speed") && map.ContainsKey("--target-duration-seconds")) throw new ArgumentException("Choose speed or target duration.");
        if (speed.HasValue && (!double.IsFinite(speed.Value) || speed <= 0 || speed > 1000) ||
            !double.IsFinite(max) || max < 1 || max > 1000 || !double.IsFinite(target) || target <= 0 ||
            !double.IsFinite(p99) || p99 <= 0 || !double.IsFinite(late) || late <= 0)
            throw new ArgumentException("Speeds and limits must be positive finite values (speed <= 1000).");
        int? limit = map.TryGetValue("--max-messages", out var count) ? int.Parse(count, CultureInfo.InvariantCulture) : null;
        if (limit is <= 0 || limit.HasValue && map.ContainsKey("--all")) throw new ArgumentException("Use --max-messages N (> 0) or --all, not both.");
        string language = map.GetValueOrDefault("--language", "csharp").ToLowerInvariant();
        if (language is not ("csharp" or "rust")) throw new ArgumentException("Language must be csharp or rust.");
        if (map.ContainsKey("--diagnostic") && (args[0] == "compare" || language == "rust")) throw new ArgumentException("GC diagnostics apply only to C#.");
        if (args[0] == "sweep" && (smoke || map.ContainsKey("--speed") || map.ContainsKey("--target-duration-seconds"))) throw new ArgumentException("Sweep chooses its own speeds and cannot be a smoke test.");
        int fragment = map.TryGetValue("--fragment-size", out var fragmentValue) ? int.Parse(fragmentValue, CultureInfo.InvariantCulture) : 0;
        if (fragment < 0) throw new ArgumentException("Fragment size cannot be negative.");
        return new(args[0], Required("--recording"), map.GetValueOrDefault("--fixture"), Path.GetFullPath(Required("--out")),
            speed, max, target, checked((long)Math.Ceiling(p99 * 1000)), checked((long)Math.Ceiling(late * 1000)),
            map.ContainsKey("--diagnostic"), smoke, map.ContainsKey("--trace"), limit, language,
            map.GetValueOrDefault("--rust-app"), map.GetValueOrDefault("--csharp-app"), map.GetValueOrDefault("--app-cpus"), fragment);
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        Options o = Parse(args);
        if (Directory.Exists(o.Output) && Directory.EnumerateFileSystemEntries(o.Output).Any())
            throw new IOException("Output directory must be empty; do not overwrite an earlier experiment.");
        Directory.CreateDirectory(o.Output);
        string? fixture = o.Fixture is null ? null : Path.GetFullPath(o.Fixture);
        string? fixtureHash = fixture is null ? null : Hash(File.ReadAllBytes(fixture));
        Recording recording;
        if (o.Recording == "demo")
        {
            var demo = DemoRecording.Create();
            recording = new Recording(demo.Id, o.MaxMessages.HasValue ? demo.Messages.Take(o.MaxMessages.Value) : demo.Messages);
        }
        else recording = await new RecordingRepository(Environment.GetEnvironmentVariable("PG_CONNECTION") ??
            AssumptionZero.AppDefaults.ConnectionString).LoadAsync(Guid.Parse(o.Recording), cancellationToken, o.MaxMessages);
        string datasetHash = DatasetHash(recording);
        Console.WriteLine($"Selected {recording.Messages.Count} messages, {recording.PayloadBytes} bytes; original duration {recording.DurationNanoseconds / 1e9:0.###} s.");
        Target[] targets = o.Command == "compare" ? [await ResolveTarget("csharp", o), await ResolveTarget("rust", o)] : [await ResolveTarget(o.Language, o)];
        string[] perf = o.Smoke ? [] : await ProbePerf();
        object host = new { Settings = HostMetadata(o.AppCpus), CpuTopology = await Command("lscpu", "--json") };
        var summaries = targets.ToDictionary(t => t.Language, _ => new List<SpeedSummary>());
        var expected = new Dictionary<string, (string Decision, string? State)>();
        bool equivalent = true;
        async Task<bool> Measure(double speed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"Replay speed {speed:R}x ({recording.DurationNanoseconds / speed / 1e9:0.###} s scheduled).");
            var runs = targets.ToDictionary(t => t.Language, _ => new List<BenchmarkRunSummary>());
            var baselines = new Dictionary<string, double>();
            foreach (Target t in targets)
            {
                if (o.Smoke) { baselines[t.Language] = 0; continue; }
                var baseline = await RunOnce(recording, speed, fixture, t, null, 0, o, [], cancellationToken);
                if (baseline.ExitCode != 0 || baseline.Replay?.Completed != true)
                    throw new InvalidOperationException($"{t.Language} uninstrumented baseline failed: {baseline.Error}");
                baselines[t.Language] = baseline.WallSeconds;
            }
            int warmup = Math.Min(recording.Messages.Count - 1, (int)Math.Ceiling(recording.Messages.Count * .1));
            int repetitions = o.Smoke ? 1 : 5;
            for (int n = 1; n <= repetitions; n++)
            {
                // Alternate which language goes first; never run consumers simultaneously.
                foreach (Target t in n % 2 == 1 ? targets : targets.Reverse())
                {
                    string directory = Path.Combine(o.Output, t.Language, $"speed-{speed.ToString("R", CultureInfo.InvariantCulture)}");
                    Directory.CreateDirectory(directory);
                    string prefix = Path.Combine(directory, $"run-{n}");
                    var once = await RunOnce(recording, speed, fixture, t, prefix, warmup, o, perf, cancellationToken);
                    if (once.App is null || once.Replay is null)
                        throw new InvalidOperationException($"{t.Language} run {n} did not produce reports: {once.Error}");
                    var app = once.App; var replay = once.Replay;
                    if (app.SchemaVersion != 3 || app.ClockFrequency != Stopwatch.Frequency)
                        throw new InvalidDataException("App report schema or monotonic clock frequency differs from simulator.");
                    if (app.BuildConfiguration != "Release") throw new InvalidDataException("Measured application must be a Release build.");
                    if (app.MemorySnapshotPhase != "after_replay_before_report")
                        throw new InvalidDataException($"Rebuild the {t.Language} Release application: memory must be sampled before report construction.");
                    var (send, processing) = BenchmarkAnalysis.Summarize(app.Frames, replay.SendStartTicks, warmup, replay.SentMessages, app.ClockFrequency);
                    bool complete = once.ExitCode == 0 && app.Completed && replay.Completed &&
                        app.ReceivedFrames == recording.Messages.Count && app.Frames.Length == recording.Messages.Count && replay.SentMessages == recording.Messages.Count &&
                        app.Frames.Select((f, i) => f.Index == i + 1 && f.Bytes == recording.Messages[i].Payload.Length && f.MessageType == recording.Messages[i].MessageType).All(x => x);
                    bool fidelity = replay.SendStartLateness.P99 <= o.ReplayLatenessLimitNs;
                    bool duration = app.MeasuredDurationNanoseconds >= 30_000_000_000L, samples = app.MeasuredFrames >= 10_000;
                    bool p99 = send.Count > 0 && send.P99 <= o.P99LimitNs && processing.P99 <= o.P99LimitNs;
                    bool diagnostic = o.Diagnostic || o.Trace;
                    bool capacity = complete && fidelity && duration && samples && p99 && !diagnostic && !o.Smoke;
                    bool smokePassed = complete && app.TotalDecisions > 0 && !string.IsNullOrEmpty(app.FinalStateHash);
                    long Rate(long count) => app.MeasuredDurationNanoseconds == 0 ? 0 : (long)(count * 1e9 / app.MeasuredDurationNanoseconds);
                    var summary = new BenchmarkRunSummary(5, t.Language, recording.Id.ToString(), fixture is null ? "synthetic" : "fixture", fixtureHash,
                        speed, app.ClockFrequency, Environment.MachineName, RuntimeInformation.OSDescription, t.Runtime, "Release", app.LogicalCpus,
                        app.ServerGc, app.GcLatencyMode,
                        warmup, diagnostic, complete, fidelity, duration, samples, p99, o.Smoke ? smokePassed : capacity, app.DecisionHash,
                        Rate(app.MeasuredFrames), Rate(app.MeasuredDecisions), replay.SentMessages, replay.SentBytes,
                        replay.ExpectedDurationNanoseconds, replay.ActualDurationNanoseconds, send, app.ReceiveToDecisionNanoseconds,
                        app.ProcessingNanoseconds, processing, replay.SendStartLateness, replay.SendDuration, BenchmarkAppOverview.From(app), once.Perf,
                        Windows(app, warmup), once.WallSeconds, complete ? null : $"exit={once.ExitCode}; {once.Error}")
                    {
                        RunKind = o.Smoke ? "smoke" : diagnostic ? "diagnostic" : "measurement", CapacityPassed = o.Smoke || diagnostic ? null : capacity,
                        FinalStateHash = app.FinalStateHash, DatasetSha256 = datasetHash, RequestedMaxMessages = o.MaxMessages,
                        P99LimitNanoseconds = o.P99LimitNs, ReplayLatenessLimitNanoseconds = o.ReplayLatenessLimitNs, Build = t.Build, Host = host
                    };
                    runs[t.Language].Add(summary);
                    await WriteJson(prefix + "-summary.json", summary);
                    Console.WriteLine($"{t.Language} run {n}: {app.ReceivedFrames} frames, {app.TotalDecisions} decisions, hash {app.DecisionHash}, complete={complete}");
                }
            }
            foreach (Target t in targets)
            {
                var rs = runs[t.Language];
                bool same = rs.Select(r => (r.DecisionHash, r.FinalStateHash)).Distinct().Count() == 1 && rs.All(r => r.FinalStateHash is not null);
                var identity = (rs[0].DecisionHash, rs[0].FinalStateHash);
                if (expected.TryGetValue(t.Language, out var prior)) same &= prior == identity;
                else expected[t.Language] = identity;
                bool passed = rs.All(r => r.Passed) && same;
                string? failure = passed ? null : string.Join(", ", new[] {
                    !same ? "decision/final-state mismatch" : null, rs.Any(r => !r.Complete) ? "incomplete" : null,
                    o.Smoke && rs.Any(r => r.Application.TotalDecisions == 0) ? "no decisions" : null,
                    !o.Smoke && rs.Any(r => !r.ReplayFidelityPassed) ? "replay lateness" : null,
                    !o.Smoke && rs.Any(r => !r.DurationPassed) ? "under 30 s" : null,
                    !o.Smoke && rs.Any(r => !r.SampleCountPassed) ? "under 10000 frames" : null,
                    !o.Smoke && rs.Any(r => !r.P99Passed) ? "P99 limit" : null,
                    !o.Smoke && (o.Diagnostic || o.Trace) ? "diagnostic runs cannot set capacity" : null }.Where(x => x is not null));
                double baseline = baselines[t.Language];
                summaries[t.Language].Add(new(speed, passed, failure, baseline <= 0 ? 0 : (rs.Average(r => r.ProcessWallSeconds) / baseline - 1) * 100, rs.ToArray()));
                Console.WriteLine($"{t.Language} {speed:0.###}x: {(passed ? "PASS" : "FAIL")} {failure}");
            }
            if (targets.Length == 2)
            {
                var all = runs.Values.SelectMany(x => x).ToArray();
                equivalent &= all.All(r => r.Complete && r.FinalStateHash is not null) && all.Select(r => (r.DecisionHash, r.FinalStateHash)).Distinct().Count() == 1;
            }
            return targets.All(t => summaries[t.Language][^1].Passed);
        }
        if (o.Command != "sweep") await Measure(o.Speed ?? Math.Clamp(recording.DurationNanoseconds / 1e9 / o.TargetSeconds, .000001, 1000));
        else
        {
            double low = 0, high = 0, speed = 1;
            while (speed <= o.MaxSpeed)
            {
                if (!await Measure(speed)) { high = speed; break; }
                low = speed;
                if (speed == o.MaxSpeed) break;
                speed = Math.Min(o.MaxSpeed, speed * 2);
            }
            for (int i = 0; i < 8 && low > 0 && high > 0 && (high - low) / low > .1; i++)
            {
                double mid = (low + high) / 2;
                if (await Measure(mid)) low = mid; else high = mid;
            }
        }
        foreach (Target t in targets)
        {
            var speeds = summaries[t.Language];
            var summary = new SweepSummary(5, recording.Id.ToString(), o.Smoke || o.Trace || o.Diagnostic ? null :
                speeds.Where(s => s.Passed).Select(s => (double?)s.Speed).Max(), speeds.ToArray());
            string dir = Path.Combine(o.Output, t.Language); Directory.CreateDirectory(dir);
            await WriteJson(Path.Combine(dir, "comparison.json"), summary);
            if (targets.Length == 1) await WriteJson(Path.Combine(o.Output, "comparison.json"), summary);
        }
        if (targets.Length == 2)
        {
            var cs = summaries["CSharp"][0].Runs; var rust = summaries["Rust"][0].Runs;
            bool valid = equivalent && !o.Smoke && !o.Trace && cs.Concat(rust).All(r => r.ReplayFidelityPassed && r.DurationPassed && r.SampleCountPassed);
            object? ratios = valid ? ComparisonMetrics(cs, rust) : null;
            await WriteJson(Path.Combine(o.Output, "paired-comparison.json"), new { SchemaVersion = 5, RecordingId = recording.Id, DatasetSha256 = datasetHash,
                ActualMessages = recording.Messages.Count, RequestedMaxMessages = o.MaxMessages, Equivalent = equivalent,
                PerformanceComparisonValid = valid, RunKind = o.Smoke ? "smoke" : o.Trace ? "diagnostic" : "measurement", Metrics = ratios,
                Languages = summaries });
            await File.WriteAllTextAsync(Path.Combine(o.Output, "report.md"), $"# C# / Rust comparison\n\n" +
                $"Messages: {recording.Messages.Count}; dataset SHA-256: `{datasetHash}`.\n\n" +
                $"Decision and final-state equivalence: **{equivalent}**. Performance comparison valid: **{valid}**.\n\n" +
                (valid ? "Ratios are CSharp/Rust: above 1 favors Rust for latency/CPU/memory; below 1 favors Rust for throughput. Per-language min/median/max describe run variation.\n\n```json\n" + JsonSerializer.Serialize(ratios, JsonOptions) + "\n```\n" :
                    "No performance ratios are published for smoke/diagnostic runs, correctness failures, short samples, or inadequate sender fidelity. Inspect per-run reports for reasons.\n") +
                "\nA fixed-speed pass establishes that tested load, not maximum capacity. Managed and allocator allocations are different measures; compare their labelled raw values.\n");
        }
        return equivalent && (!o.Smoke && (o.Diagnostic || o.Trace) ? summaries.Values.SelectMany(x => x).SelectMany(x => x.Runs).All(r => r.Complete) : summaries.Values.All(x => x.Any(s => s.Passed))) ? 0 : 2;
    }

    private static object ComparisonMetrics(BenchmarkRunSummary[] cs, BenchmarkRunSummary[] rust)
    {
        var selectors = new Dictionary<string, Func<BenchmarkRunSummary, double>> {
            ["SendToDecisionP99Ns"] = r => r.SendToDecisionNanoseconds.P99, ["ReceiveToDecisionP99Ns"] = r => r.ReceiveToDecisionNanoseconds.P99,
            ["SendToProcessingP99Ns"] = r => r.SendToProcessingNanoseconds.P99, ["ProcessingP99Ns"] = r => r.ProcessingNanoseconds.P99,
            ["DecodeP99Ns"] = r => r.Application.DecodeNanoseconds.P99, ["BookP99Ns"] = r => r.Application.BookNanoseconds.P99,
            ["SignalP99Ns"] = r => r.Application.SignalNanoseconds.P99, ["QuoteP99Ns"] = r => r.Application.QuoteNanoseconds.P99,
            ["FramesPerSecond"] = r => r.AchievedFramesPerSecond, ["DecisionsPerSecond"] = r => r.AchievedDecisionsPerSecond,
            ["CpuNanosecondsPerFrame"] = r => r.Application.CpuNanosecondsPerFrame, ["ResidentBytes"] = r => r.Application.ResidentBytes,
            ["PeakResidentBytes"] = r => r.Application.PeakResidentBytes };
        return selectors.ToDictionary(k => k.Key, k => {
            double[] a = cs.Select(k.Value).Order().ToArray(), b = rust.Select(k.Value).Order().ToArray();
            return new { CSharp = new { Minimum = a[0], Median = a[a.Length / 2], Maximum = a[^1] },
                Rust = new { Minimum = b[0], Median = b[b.Length / 2], Maximum = b[^1] },
                CSharpOverRust = b[b.Length / 2] == 0 ? (double?)null : a[a.Length / 2] / b[b.Length / 2] };
        });
    }
    public static string DatasetHash(Recording recording)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> bytes = stackalloc byte[25];
        foreach (var m in recording.Messages)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes, m.SequenceNumber);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], m.ElapsedNanoseconds);
            bytes[16] = (byte)m.MessageType;
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(bytes[17..], m.Payload.Length);
            hash.AppendData(bytes); hash.AppendData(m.Payload);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static Task WriteJson(string path, object value) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions));
    private static async Task<Target> ResolveTarget(string language, Options o)
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        string binary = language == "csharp" ? o.CSharpApp ?? Path.Combine(root, "AssumptionZero.App/bin/Release/net10.0/AssumptionZero.App.dll") :
            o.RustApp ?? throw new ArgumentException("--rust-app is required for Rust or compare.");
        binary = Path.GetFullPath(binary);
        if (!File.Exists(binary)) throw new FileNotFoundException("Build the Release application first.", binary);
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
            (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet" ? Environment.ProcessPath! : "dotnet");
        string runtime = language == "csharp" ? RuntimeInformation.FrameworkDescription : await Command(binary, "--version");
        string source = language == "csharp" ? root : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(binary)!, "../.."));
        object build = new { BinarySha256 = Hash(File.ReadAllBytes(binary)), SourceRevision = await Command("git", "-C", source, "rev-parse", "HEAD"),
            SourceStatus = await Command("git", "-C", source, "status", "--porcelain"), BuildManifest = ReadOptional(binary + ".build.json"),
            Compiler = language == "csharp" ? await Command(dotnet, "--version") : runtime };
        return new(language == "csharp" ? "CSharp" : "Rust", language == "csharp" ? dotnet : binary,
            language == "csharp" ? [binary] : [], binary, runtime, build);
    }
    private static async Task<string> Command(string command, params string[] args)
    {
        try { using var p = new Process { StartInfo = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (string arg in args) p.StartInfo.ArgumentList.Add(arg);
            p.Start(); var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync(); await stderr; return p.ExitCode == 0 ? (await stdout).Trim() : "unavailable";
        } catch { return "unavailable"; }
    }
    private static string? ReadOptional(string path) { try { return File.ReadAllText(path); } catch { return null; } }
    private static object HostMetadata(string? appCpus) => new {
        ApplicationCpuSet = appCpus, SimulatorStatus = ReadOptional("/proc/self/status")?.Split('\n').Where(s => s.StartsWith("Cpus_allowed_list:") || s.StartsWith("Mems_allowed_list:")).ToArray(),
        CpuModel = ReadOptional("/proc/cpuinfo")?.Split('\n').FirstOrDefault(s => s.StartsWith("model name")),
        Governor = ReadOptional("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor"),
        PerfEventParanoid = ReadOptional("/proc/sys/kernel/perf_event_paranoid"),
        DotnetTieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO"), DotnetServerGc = Environment.GetEnvironmentVariable("DOTNET_gcServer"),
        DotnetProcessorCount = Environment.GetEnvironmentVariable("DOTNET_PROCESSOR_COUNT") };
    private static ThroughputWindow[] Windows(BenchmarkAppReport app, int warmup)
    {
        if (app.MeasuredFrames == 0) return [];
        long origin = app.Frames[warmup].ReceiveTicks; var windows = new List<ThroughputWindow>();
        for (int i = warmup; i < app.Frames.Length; i++) {
            FrameSample f = app.Frames[i]; int second = (int)((f.ReceiveTicks - origin) / app.ClockFrequency);
            while (windows.Count <= second) windows.Add(new(windows.Count, 0, 0, 0));
            var old = windows[second]; windows[second] = old with { Frames = old.Frames + 1, Bytes = old.Bytes + f.Bytes, Decisions = old.Decisions + f.Decisions };
        } return windows.ToArray();
    }
    private sealed record OnceResult(BenchmarkAppReport? App, ReplayReport? Replay, PerfCounters Perf, double WallSeconds, int ExitCode, string? Error);
    private static async Task<OnceResult> RunOnce(Recording recording, double speed, string? fixture, Target target,
        string? prefix, int warmup, Options o, string[] perfEvents, CancellationToken cancellationToken)
    {
        var source = new TaskCompletionSource<ReplayReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = ReplayServer.Create(recording, "http://127.0.0.1:0", r => source.TrySetResult(r), o.FragmentSize, speed);
        await server.StartAsync(cancellationToken);
        string url = server.Urls.Single().Replace("http://", "ws://") + "/stream";
        var command = new List<string>();
        if (o.AppCpus is not null) command.AddRange(["taskset", "--cpu-list", o.AppCpus]);
        bool usePerf = prefix is not null && perfEvents.Length > 0;
        if (usePerf) command.AddRange(["perf", "stat", "-x,", "-e", string.Join(',', perfEvents), "-o", prefix + "-perf.csv", "--"]);
        command.Add(target.Executable); command.AddRange(target.Arguments);
        using var process = new Process { StartInfo = new ProcessStartInfo(command[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        var start = process.StartInfo; foreach (string arg in command.Skip(1)) start.ArgumentList.Add(arg);
        foreach (string key in new[] { "BENCHMARK_OUTPUT", "BENCHMARK_EXPECTED_FRAMES", "BENCHMARK_WARMUP_FRAMES", "BENCHMARK_DIAGNOSTIC", "CORRECTNESS_TRACE", "MEASURE_ALLOCATIONS", "REPLAY_FIXTURE" }) start.Environment.Remove(key);
        start.Environment["EXECUTION_MODE"] = "Replay"; start.Environment["REPLAY_URL"] = url; start.Environment["LOG_EVENTS"] = "false";
        if (fixture is not null) start.Environment["REPLAY_FIXTURE"] = fixture;
        if (prefix is not null) {
            start.Environment["BENCHMARK_OUTPUT"] = prefix + "-samples.json";
            start.Environment["BENCHMARK_EXPECTED_FRAMES"] = recording.Messages.Count.ToString(CultureInfo.InvariantCulture);
            start.Environment["BENCHMARK_WARMUP_FRAMES"] = warmup.ToString(CultureInfo.InvariantCulture);
            if (o.Diagnostic) start.Environment["BENCHMARK_DIAGNOSTIC"] = "1";
            if (o.Trace) start.Environment["CORRECTNESS_TRACE"] = prefix + "-decisions.jsonl";
        }
        var wall = Stopwatch.StartNew(); process.Start();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(60, recording.DurationNanoseconds / speed / 1e9 * 3 + 30)));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        wall.Stop(); string output = await stdout, error = await stderr;
        if (prefix is not null) { await File.WriteAllTextAsync(prefix + "-stdout.txt", output); await File.WriteAllTextAsync(prefix + "-stderr.txt", error); }
        cancellationToken.ThrowIfCancellationRequested();
        ReplayReport? replay = null;
        try { replay = await source.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); } catch (TimeoutException) { }
        BenchmarkAppReport? app = null;
        if (prefix is not null && File.Exists(prefix + "-samples.json")) {
            app = JsonSerializer.Deserialize<BenchmarkAppReport>(await File.ReadAllTextAsync(prefix + "-samples.json"));
            if (app is not null) { await WriteSamplesCsv(prefix + "-samples.csv", app.Frames, replay?.SendStartTicks); await WriteJson(prefix + "-gc-pauses.json", app.GcPauses); File.Delete(prefix + "-samples.json"); }
        }
        return new(app, replay, usePerf ? ParsePerf(prefix + "-perf.csv") : new("whole_process", null, null, null, null, null,
            o.Smoke ? "not collected in smoke mode" : "perf hardware counters unavailable or disabled"), wall.Elapsed.TotalSeconds, process.ExitCode, error);
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
