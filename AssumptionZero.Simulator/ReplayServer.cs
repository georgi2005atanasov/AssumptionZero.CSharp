using System.Net.WebSockets;
using System.Text.Json;

namespace AssumptionZero.Simulator;

public static class ReplayServer
{
    public static WebApplication Create(Recording recording, string url = "http://127.0.0.1:5080",
        Action<ReplayReport>? reportCompleted = null, int fragmentSize = 0, double speed = 1)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        // Avoid request logging in the measured path.
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
        int active = 0;

        app.MapGet("/stream", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }

            ReplayReport? report = null;
            try
            {
                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                using var session = CancellationTokenSource.CreateLinkedTokenSource(
                    context.RequestAborted, app.Lifetime.ApplicationStopping);
                // Receive concurrently so a peer close interrupts even a long recorded gap.
                var receiveTask = WatchClientAsync(socket, session);
                try
                {
                    report = await Task.Factory.StartNew(
                        () => new ReplayEngine(new StopwatchReplayClock()).Run(recording,
                            (message, token) => SendMessage(socket, message, fragmentSize, token), session.Token, speed),
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await socket.CloseOutputAsync(
                            report.Completed ? WebSocketCloseStatus.NormalClosure : WebSocketCloseStatus.EndpointUnavailable,
                            report.Completed ? "Replay complete" : "Replay interrupted", closeTimeout.Token);
                        await receiveTask.WaitAsync(closeTimeout.Token);
                    }
                }
                finally
                {
                    await session.CancelAsync();
                    await receiveTask;
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException)
            {
                Console.Error.WriteLine($"Replay connection ended: {exception.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref active, 0);
                if (report is not null) (reportCompleted ?? PrintReport)(report);
            }
        });
        return app;
    }

    private static void SendMessage(WebSocket socket, ReplayMessage message, int fragmentSize, CancellationToken token)
    {
        if (fragmentSize <= 0 || message.Payload.Length <= fragmentSize)
        {
            socket.SendAsync(message.Payload.AsMemory(), message.MessageType, true, token)
                .AsTask().GetAwaiter().GetResult();
            return;
        }
        for (int offset = 0; offset < message.Payload.Length; offset += fragmentSize)
        {
            int length = Math.Min(fragmentSize, message.Payload.Length - offset);
            socket.SendAsync(message.Payload.AsMemory(offset, length), message.MessageType,
                offset + length == message.Payload.Length, token).AsTask().GetAwaiter().GetResult();
        }
    }

    private static async Task WatchClientAsync(WebSocket socket, CancellationTokenSource session)
    {
        try
        {
            var buffer = new byte[256];
            while (!session.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), session.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                // This server replays a fixed capture; client subscription commands are unsupported.
                throw new IOException("Replay does not accept subscription or data messages.");
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException)
        {
            // The timing worker reports cancellation and the number of successfully sent messages.
        }
        finally
        {
            await session.CancelAsync();
        }
    }

    public static void PrintReport(ReplayReport report) => Console.WriteLine(
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
