using AssumptionZero.Application.Interfaces.External;
using AssumptionZero.Application.Interfaces.Services;
using AssumptionZero.Application.Services;

namespace AssumptionZero.App;

internal sealed class LiveExecutionSession(
    ILiveBootstrap bootstrap,
    AccountRiskLedger ledger,
    IInstrumentRuntime[] runtimes,
    IAccountStream accountStream,
    IDataFeedReader feed)
{

    public async Task Run(CancellationToken cancellationToken)
    {
        bool first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ledger.Invalidate();
                await bootstrap.LoadAccount(ledger, runtimes, cancellationToken, initializeInventory: first);
                first = false;
                await feed.Start(cancellationToken, afterConnect: LoadBooks);
                break;
            }
            catch (IOException exception) when (!cancellationToken.IsCancellationRequested && !accountStream.Faulted)
            {
                ledger.Invalidate();
                foreach (IInstrumentRuntime runtime in runtimes)
                {
                    runtime.Invalidate();
                }

                Console.Error.WriteLine($"Market feed resynchronizing: {exception.Message}");
                await Task.Delay(1_000, cancellationToken);
            }
        }
    }

    private Task LoadBooks(CancellationToken cancellationToken) => bootstrap.LoadBooks(runtimes, cancellationToken);
}
