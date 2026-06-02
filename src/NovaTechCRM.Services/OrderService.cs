using Microsoft.Extensions.Logging;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Repositories;

namespace NovaTechCRM.Services;

public class OrderService
{
    private readonly IOrderRepository _orderRepo;
    private readonly IFraudShieldService _fraudShield;
    private readonly INotificationService _notifications;
    private readonly ILogger<OrderService> _logger;

    public OrderService(
        IOrderRepository orderRepo,
        IFraudShieldService fraudShield,
        INotificationService notifications,
        ILogger<OrderService> logger)
    {
        _orderRepo = orderRepo;
        _fraudShield = fraudShield;
        _notifications = notifications;
        _logger = logger;
    }

    public async Task<Order> PlaceOrderAsync(Order order, CancellationToken ct = default)
    {
        order.Status = OrderStatus.FraudCheckPending;
        await _orderRepo.SaveAsync(order, ct);

        // Decouple from the HTTP request's CancellationToken here. Once the order is
        // persisted, the fraud check and fulfillment must complete regardless of whether
        // the client disconnects. Passing the request CT to CheckAsync caused ~$500+ orders
        // (Medium/High risk, slower fraud API path) to be permanently stuck in FraudCheckPending
        // whenever a client timeout or load-balancer reset cancelled the token mid-flight.
        // The OperationCanceledException was silently swallowed at the Kestrel level (no error logs).
        var fraudResult = await _fraudShield.CheckAsync(order, CancellationToken.None);

        if (!fraudResult.Passed)
        {
            order.Status = OrderStatus.Rejected;
            order.FraudCheckPassed = false;
            await _orderRepo.SaveAsync(order, CancellationToken.None);
            await _notifications.SendFraudAlertAsync(order, fraudResult, CancellationToken.None);
            _logger.LogWarning("Order {OrderId} rejected by FraudShield: risk={RiskLevel}, reason={Reason}",
                order.Id, fraudResult.RiskLevel, fraudResult.Reason);
            return order;
        }

        order.FraudCheckId = fraudResult.CheckId;
        order.FraudCheckPassed = true;
        await FulfillOrderAsync(order);

        return order;
    }

    private async Task FulfillOrderAsync(Order order)
    {
        order.Status = OrderStatus.Fulfilled;
        order.FulfilledAt = DateTime.UtcNow;
        await _orderRepo.SaveAsync(order, CancellationToken.None);

        await _notifications.SendOrderConfirmationAsync(order, CancellationToken.None);

        _logger.LogInformation("Order {OrderId} fulfilled for customer {CustomerId} (amount: {Amount:C})",
            order.Id, order.CustomerId, order.TotalAmount);
    }

    public async Task<Order?> GetOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        return await _orderRepo.GetByIdAsync(orderId, ct);
    }

    public async Task<IReadOnlyList<Order>> GetCustomerOrdersAsync(string customerId, CancellationToken ct = default)
    {
        return await _orderRepo.GetByCustomerAsync(customerId, ct);
    }
}
