using AssumptionZero.Domain.Entities;

namespace AssumptionZero.Application.Interfaces.Services;

/// <summary>
/// Used for pushing orders for an instrument through
/// single-producer single-consumer queue
/// </summary>
public interface IOrderService
{
    void PlaceOrder(Order order);
    void CancelOrder(Order order);
}