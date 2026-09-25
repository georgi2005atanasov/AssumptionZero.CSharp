using AssumptionZero.Simulator;

if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine("""
                      Usage:
                        AssumptionZero.Simulator demo [http://127.0.0.1:5080]
                        AssumptionZero.Simulator list
                        AssumptionZero.Simulator replay <recording-id> [http://127.0.0.1:5080]
                      demo needs no database. Set PG_CONNECTION for list and replay.
                      Replay sends the entire recording at 1x over /stream; stream query parameters are ignored.
                      """);
    return args.Length == 0 ? 1 : 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    bool list = args is ["list"];
    bool demo = args is ["demo"] or ["demo", _];
    bool replay = args.Length is 2 or 3 && args[0] == "replay" && Guid.TryParse(args[1], out _);
    if (!list && !demo && !replay)
        throw new ArgumentException("Use 'demo', 'list', or 'replay <recording-id> [listen-url]'.");
    RecordingRepository? repository = null;
    if (!demo)
    {
        string connectionString = Environment.GetEnvironmentVariable("PG_CONNECTION") ??
                                  "Host=172.17.0.2;Database=assumption_zero;Username=postgres;Password=mysecretpassword;Trust Server Certificate=true;";
        repository = new RecordingRepository(connectionString);
    }
    if (list)
    {
        Console.WriteLine("Recording ID                           Messages       Payload bytes       Duration (ns)");
        foreach (var recording in await repository!.ListAsync(cancellation.Token))
            Console.WriteLine(
                $"{recording.Id} {recording.MessageCount,12} {recording.PayloadBytes,19} {recording.DurationNanoseconds,19}");
        return 0;
    }

    var loaded = demo ? DemoRecording.Create() :
        await repository!.LoadAsync(Guid.Parse(args[1]), cancellation.Token);
    string url = demo ? (args.Length == 2 ? args[1] : "http://127.0.0.1:5080") :
        (args.Length == 3 ? args[2] : "http://127.0.0.1:5080");
    await using var app = ReplayServer.Create(loaded, url);
    await app.StartAsync(cancellation.Token);
    Console.WriteLine(
        $"Loaded {loaded.Id}: {loaded.Messages.Count} messages, {loaded.PayloadBytes} payload bytes, {loaded.DurationNanoseconds} ns.");
    Console.WriteLine($"Listening on {url}/stream. One active client; each connection replays from the beginning.");
    await app.WaitForShutdownAsync(cancellation.Token);
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.WriteLine("Stopped.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
