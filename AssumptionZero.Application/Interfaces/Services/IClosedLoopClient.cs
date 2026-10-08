namespace AssumptionZero.Application.Interfaces.Services;

/// <summary>Runs a simulated exchange session through settlement and writes its report.</summary>
public interface IClosedLoopClient
{
    Task Run(string url, string output, CancellationToken cancellationToken);
}
