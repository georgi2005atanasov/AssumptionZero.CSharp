using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AssumptionZero.Infrastructure.Simulation;
using static AssumptionZero.Infrastructure.Simulation.ExchangeProtocol;
namespace AssumptionZero.Simulator;

public static class ClosedLoopRunner
{
    public static async Task<int> Run(string[] args, CancellationToken ct)
    {
        var o = new Dictionary<string, string>();
        for (int i = 0; i < args.Length; i++)
        { if (!args[i].StartsWith("--") || i + 1 == args.Length || !o.TryAdd(args[i], args[++i])) throw new ArgumentException("Options must be unique --name value pairs."); }
        string[] known = ["--max-messages", "--repetitions", "--target-duration-seconds", "--rust-root", "--out", "--recording", "--csharp-app", "--rust-app", "--language", "--scenario", "--app-cpus", "--check-only"];
        if (o.Keys.Except(known).Any()) throw new ArgumentException("Unknown option: " + o.Keys.Except(known).First());
        if (o.TryGetValue("--scenario", out var scenario) && scenario is not ("mixed" or "rest")) throw new ArgumentException("Scenario must be mixed or rest.");
        if (o.TryGetValue("--check-only", out var check) && check is not ("true" or "false")) throw new ArgumentException("check-only must be true or false.");
        string Get(string k, string value) => o.GetValueOrDefault(k, value);
        int count = int.Parse(Get("--max-messages", "500000")), reps = int.Parse(Get("--repetitions", "5")); double seconds = double.Parse(Get("--target-duration-seconds", "600"), System.Globalization.CultureInfo.InvariantCulture);
        if (count < 100 || reps < 1 || seconds <= 0 || !double.IsFinite(seconds)) throw new ArgumentException("Invalid workload parameters.");
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
        string rustRoot = Get("--rust-root", "/home/sparrow/CLionProjects/assumption-zero");
        string output = Path.GetFullPath(Get("--out", Path.Combine(root, "benchmark-results", DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-closed-loop-" + Guid.NewGuid().ToString("N")[..6])));
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Output directory must be empty.");
        Directory.CreateDirectory(output); Console.WriteLine($"Results: {output}");
        var recording = Get("--recording", "fc401a92-7803-486e-8940-5db1eebc790c") == "demo" ? DemoRecording.Create() : await new RecordingRepository(Environment.GetEnvironmentVariable("PG_CONNECTION") ?? AssumptionZero.AppDefaults.ConnectionString).LoadAsync(Guid.Parse(Get("--recording", "fc401a92-7803-486e-8940-5db1eebc790c")), ct, count);
        if (recording.Messages.Count < count) throw new InvalidDataException($"Requested {count} messages but recording has {recording.Messages.Count}.");
        if (recording.Messages.Count > count) recording = new(recording.Id, recording.Messages.Take(count));
        if (recording.DurationNanoseconds <= 0) throw new InvalidDataException("Recording must span positive elapsed time.");
        var symbols = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(Path.Combine(rustRoot, "src/symbols.json"), ct))!;
        string csharp = Get("--csharp-app", Path.Combine(root, "AssumptionZero.App/bin/Release/net10.0/AssumptionZero.App.dll"));
        string rust = Get("--rust-app", Path.Combine(rustRoot, "target/release/assumption-zero"));
        var languages = Get("--language", "both") == "both" ? new[] { "CSharp", "Rust" } : new[] { Get("--language", "CSharp") };
        if (languages.Any(l => l is not ("CSharp" or "Rust"))) throw new ArgumentException("Language must be CSharp, Rust, or both.");
        var artifacts = new List<object>(); string? referenceTrace = null, referenceState = null;
        async Task<JsonElement> Once(string language, string mode, string label)
        {
            var manifest = ClosedLoopSession.Manifest(recording, symbols, mode, Get("--scenario", "mixed"));
            var session = new ClosedLoopSession(recording, manifest, seconds); await using var app = session.Create(); await app.StartAsync(ct);
            string url = app.Urls.Single().Replace("http://", "ws://"), file = Path.Combine(output, $"{label}-{language}.json");
            string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "/home/sparrow/.dotnet/dotnet";
            var cmd = language == "CSharp" ? new List<string> { dotnet, csharp } : new List<string> { rust };
            if (o.TryGetValue("--app-cpus", out var cpus)) cmd.InsertRange(0, new[] { "taskset", "--cpu-list", cpus });
            var info = new ProcessStartInfo(cmd[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }; foreach (var a in cmd.Skip(1)) info.ArgumentList.Add(a);
            info.Environment["SIM_MEASURE"] = label == "baseline" || mode == "deterministic" ? "false" : "true";
            info.Environment["EXECUTION_MODE"] = "SimulatedExchange"; info.Environment["SIM_EXCHANGE_URL"] = url; info.Environment["SIM_OUTPUT"] = file;
            using var child = new Process { StartInfo = info }; child.Start(); var stdout = child.StandardOutput.ReadToEndAsync(ct); var stderr = child.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(mode == "deterministic" ? Math.Max(180, recording.Messages.Count / 100.0) : seconds * 3 + 60));
            try
            {
                var serving = session.Run(timeout.Token); var exiting = child.WaitForExitAsync(timeout.Token);
                if (await Task.WhenAny(serving, exiting) == exiting && child.ExitCode != 0) { await timeout.CancelAsync(); try { await serving; } catch { } throw new InvalidOperationException($"{language} exited {child.ExitCode}: {await stderr}"); }
                await serving; await exiting;
                string errors = await stderr; await File.WriteAllTextAsync(Path.Combine(output, $"{label}-{language}.log"), (await stdout) + errors, ct);
                if (child.ExitCode != 0) throw new InvalidOperationException($"{language} exited {child.ExitCode}: {errors}");
                var result = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct)).RootElement.Clone();
                if (result.GetProperty("frames").GetInt64() != count || !result.GetProperty("completed").GetBoolean()) throw new InvalidDataException("Incomplete client workload.");
                string trace = result.GetProperty("trace_hash").GetString()!, state = Canonical(result.GetProperty("state"));
                if (mode == "deterministic")
                {
                    referenceTrace ??= trace; referenceState ??= state;
                    if (trace != referenceTrace || state != referenceState) throw new InvalidDataException($"{language} deterministic trace/state mismatch; inspect {file}.");
                }
                var expected = session.Exchange.Snapshot(); var balances = result.GetProperty("state").GetProperty("balances");
                for (int i = 0; i < expected.Length; i++)
                    if (balances[i].GetProperty("free").GetString() != expected[i].Free || balances[i].GetProperty("locked").GetString() != expected[i].Locked || balances[i].GetProperty("reserved").GetString() != "0") throw new InvalidDataException("Client/exchange ledger mismatch.");
                if (result.GetProperty("reports").GetInt64() != session.Exchange.Sequence || session.Exchange.OpenOrders != 0) throw new InvalidDataException("Unsettled exchange session.");
                var lateness = session.SenderLateness.Order().ToArray();
                var details = new { manifest, accepted = session.Exchange.Accepted, rejected = session.Exchange.Rejected, fills = session.Exchange.Fills, cancels = session.Exchange.Cancels, fees = Money(session.Exchange.Fees), sender_lateness_p99_ns = lateness.Length == 0 ? 0 : lateness[(int)Math.Ceiling(lateness.Length * .99) - 1] };
                await File.WriteAllTextAsync(Path.Combine(output, $"{label}-{language}-exchange.json"), JsonSerializer.Serialize(details, Json), ct);
                artifacts.Add(new { language, mode, label, file = Path.GetFileName(file), trace_hash = trace, commands = result.GetProperty("commands").GetInt64(), reports = session.Exchange.Sequence });
                Console.WriteLine($"{label} {language}: {count} frames, {session.Exchange.Accepted} accepted, {session.Exchange.Fills} fills, reconciled."); return result;
            }
            catch
            {
                if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(CancellationToken.None);
                string error = await stderr; await File.WriteAllTextAsync(file + ".error.log", (await stdout) + error, CancellationToken.None); throw;
            }
            finally { await app.StopAsync(CancellationToken.None); }
        }
        foreach (var language in languages) await Once(language, "deterministic", "correctness");
        foreach (var language in languages.Reverse()) await Once(language, "deterministic", "repeat-correctness");
        if (Get("--check-only", "false") != "true")
        {
            foreach (var language in languages) await Once(language, "async", "baseline");
            for (int n = 1; n <= reps; n++) foreach (var language in n % 2 == 1 ? languages : languages.Reverse()) await Once(language, "async", $"run-{n}");
        }
        var coverage = recording.Messages.GroupBy(m => ClosedLoopSession.Metadata(m.Payload).Symbol).ToDictionary(g => g.Key, g => g.LongCount());
        var summary = new
        {
            schema_version = 1,
            kind = "controlled_synthetic_closed_loop",
            recording_id = recording.Id,
            messages = count,
            target_seconds = seconds,
            warmup_messages = count / 10,
            measured_messages = count - count / 10,
            coverage = symbols.Select(s => new { symbol = s, frames = coverage.GetValueOrDefault(s) }).ToArray(),
            synthetic_delay = new { ack = 0, partial = 2, full = 4, deterministic_units = "replay frames", async_units = "milliseconds" },
            dataset_sha256 = DatasetHash(recording),
            csharp_sha256 = File.Exists(csharp) ? Hash(File.ReadAllBytes(csharp)) : null,
            rust_sha256 = File.Exists(rust) ? Hash(File.ReadAllBytes(rust)) : null,
            exact_deterministic_equivalence = languages.Length == 2,
            performance_ratios_published = false,
            interpretation = "Synthetic execution mechanics, not historical fills. Asynchronous workloads may diverge; compare counts and readiness before resource/latency conclusions.",
            runs = artifacts
        };
        await File.WriteAllTextAsync(Path.Combine(output, "closed-loop-comparison.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions(Json) { WriteIndented = true }), ct);
        Console.WriteLine($"Complete. Return this results directory: {output}"); return 0;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Canonical(JsonElement element)
    {
        object? ConvertElement(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Object => e.EnumerateObject().OrderBy(x => x.Name).ToDictionary(x => x.Name, x => ConvertElement(x.Value)),
            JsonValueKind.Array => e.EnumerateArray().Select(ConvertElement).ToArray(),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => e.GetDecimal(),
            _ => null
        };
        return JsonSerializer.Serialize(ConvertElement(element));
    }
    private static string DatasetHash(Recording r)
    { using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); foreach (var m in r.Messages) { h.AppendData(BitConverter.GetBytes(m.SequenceNumber)); h.AppendData(BitConverter.GetBytes(m.ElapsedNanoseconds)); h.AppendData(m.Payload); } return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant(); }
}
