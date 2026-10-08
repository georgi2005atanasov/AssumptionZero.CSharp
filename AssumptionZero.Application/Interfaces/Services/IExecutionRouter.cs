namespace AssumptionZero.Application.Interfaces.Services;

/// <summary>Starts market capture, live processing, or recorded market replay.</summary>
public interface IExecutionRouter
{
    Task SeedData(
        string apiKey,
        string connectionString,
        string[] symbols,
        bool logEvents,
        CancellationToken cancellationToken);
    Task NormalExecution(string[] symbols, bool logEvents, CancellationToken cancellationToken);
    Task Replay(string[] symbols, bool logEvents, CancellationToken cancellationToken);
}
