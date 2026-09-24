namespace AssumptionZero.Application.Interfaces.External;

public interface IOrderEntryClient
{
    Task PlaceOrder(); // todo: decide the order structure
    Task CancelOrder();
}