using AssumptionZero.Application.Interfaces.Services;
using AssumptionZero.Application.Services;
using AssumptionZero.Domain.Entities;

namespace AssumptionZero.Application.Interfaces.External;

/// <summary>Loads exchange instruments, reconciles accounts and books, and signs startup requests.</summary>
public interface ILiveBootstrap : IDisposable
{
    Task<Instrument[]> LoadInstruments(string[] wanted, CancellationToken cancellationToken);
    Task LoadAccount(
        AccountRiskLedger ledger,
        IInstrumentRuntime[] runtimes,
        CancellationToken cancellationToken,
        bool initializeInventory = false);
    Task LoadBooks(IInstrumentRuntime[] runtimes, CancellationToken cancellationToken);
    Task<string> Sign(string payload, CancellationToken cancellationToken);
}
