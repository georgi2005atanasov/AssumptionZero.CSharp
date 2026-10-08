namespace AssumptionZero.Application.Interfaces.External;

/// <summary>Queues account events for application by the single trading-state owner.</summary>
public interface IAccountStream : IAsyncDisposable
{
    bool Faulted { get; }

    Task Start(CancellationToken cancellationToken);
    void Drain();
}
