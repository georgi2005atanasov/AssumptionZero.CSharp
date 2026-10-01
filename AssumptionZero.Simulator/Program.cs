using AssumptionZero.Simulator;
using System.Globalization;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine("""
        Usage:
          AssumptionZero.Simulator list
          AssumptionZero.Simulator demo [listen-url] [--speed N]
          AssumptionZero.Simulator replay RECORDING_UUID [listen-url]
            [--max-messages N | --all] [--speed N | --target-duration-seconds N]
          AssumptionZero.Simulator benchmark run|sweep|compare --recording UUID|demo --out DIR
            [--max-messages N | --all] [--speed N | --target-duration-seconds 60]
            [--language csharp|rust] [--rust-app PATH] [--csharp-app PATH] [--fixture PATH]
            [--p99-limit-us 1000] [--replay-p99-lateness-us 100] [--max-speed 16]
            [--smoke] [--trace] [--diagnostic] [--app-cpus CPU-LIST] [--fragment-size N]
        Omitted limits select the whole recording. Standalone replay defaults to 1x;
        benchmarks target 60 seconds. Smoke defaults to 1000x and never sets capacity.
        PostgreSQL is needed only to load/list recordings. Demo needs no database.
        """);
    return args.Length == 0 ? 1 : 0;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (args[0] == "benchmark") return await BenchmarkRunner.RunAsync(args[1..], cancellation.Token);
    string connection = Environment.GetEnvironmentVariable("PG_CONNECTION") ?? AssumptionZero.AppDefaults.ConnectionString;
    var repository = new RecordingRepository(connection);
    if (args is ["list"])
    {
        Console.WriteLine("Recording ID                           Messages       Payload bytes       Duration (ns)");
        foreach (var r in await repository.ListAsync(cancellation.Token))
            Console.WriteLine($"{r.Id} {r.MessageCount,12} {r.PayloadBytes,19} {r.DurationNanoseconds,19}");
        return 0;
    }
    bool demo = args[0] == "demo";
    if (!demo && (args[0] != "replay" || args.Length < 2 || !Guid.TryParse(args[1], out _)))
        throw new ArgumentException("Use demo, list, replay UUID, or benchmark. See --help.");
    int start = demo ? 1 : 2;
    string url = "http://127.0.0.1:5080";
    if (start < args.Length && !args[start].StartsWith("--")) url = args[start++];
    int? max = null; bool all = false; double? speed = null, duration = null;
    var seen = new HashSet<string>();
    for (int i = start; i < args.Length; i++)
    {
        string key = args[i];
        if (!seen.Add(key)) throw new ArgumentException($"Duplicate option {key}.");
        if (key == "--all") { all = true; continue; }
        if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {key}.");
        string value = args[++i];
        switch (key)
        {
            case "--max-messages": max = int.Parse(value, CultureInfo.InvariantCulture); break;
            case "--speed": speed = double.Parse(value, CultureInfo.InvariantCulture); break;
            case "--target-duration-seconds": duration = double.Parse(value, CultureInfo.InvariantCulture); break;
            default: throw new ArgumentException($"Unknown option {key}.");
        }
    }
    if (max is <= 0 || all && max.HasValue) throw new ArgumentException("Use --max-messages N (> 0) or --all, not both.");
    if (speed.HasValue && duration.HasValue || speed.HasValue && (!double.IsFinite(speed.Value) || speed <= 0 || speed > 1000) ||
        duration.HasValue && (!double.IsFinite(duration.Value) || duration <= 0)) throw new ArgumentException("Invalid replay timing options.");
    Recording recording;
    if (demo) { var d = DemoRecording.Create(); recording = new(d.Id, max.HasValue ? d.Messages.Take(max.Value) : d.Messages); }
    else recording = await repository.LoadAsync(Guid.Parse(args[1]), cancellation.Token, max);
    double rate = speed ?? (duration.HasValue ? Math.Clamp(recording.DurationNanoseconds / 1e9 / duration.Value, .000001, 1000) : 1);
    await using var app = ReplayServer.Create(recording, url, speed: rate);
    await app.StartAsync(cancellation.Token);
    Console.WriteLine($"Loaded {recording.Id}: {recording.Messages.Count} messages, {recording.PayloadBytes} bytes, speed {rate:R}x.");
    Console.WriteLine($"Listening on {url}/stream. Each connection starts at sequence 1; one active client.");
    await app.WaitForShutdownAsync(cancellation.Token);
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Console.WriteLine("Stopped."); return 130; }
catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
