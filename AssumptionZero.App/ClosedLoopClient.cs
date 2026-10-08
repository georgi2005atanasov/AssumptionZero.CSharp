using AssumptionZero.Application.Interfaces.Services;

namespace AssumptionZero.App;

public sealed class ClosedLoopClient : IClosedLoopClient
{

    public Task Run(string url, string output, CancellationToken cancellationToken)
    {
        return new ClosedLoopClientSession().Run(url, output, cancellationToken);
    }
}
