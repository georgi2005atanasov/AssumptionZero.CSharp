using AssumptionZero.Domain.Entities;
using AssumptionZero.Domain.InMemory;

namespace AssumptionZero.Application.Interfaces.Services;

public interface IInstrumentRuntime
{
    public Instrument Instrument { get; set; }

    public OrderBook OrderBook { get; set; }

    public SignalState Signals { get; set; }

    public SignalMemory SignalMemory { get; set; }

    public PricingState PricingState { get; set; }

    public InventoryState Inventory { get; set; }

    public WorkingOrders WorkingOrders { get; set; }

    public IStrategy Strategy { get; set; }
}

// MarketState.Apply(marketEvent);
//
// Signals.Update(MarketState);
//
// var decision = Strategy.Evaluate(
//     MarketState,
//     Signals,
//     Inventory,
//     WorkingOrders);
// }
// orderEntryClient.ExecuteDecision(decision);