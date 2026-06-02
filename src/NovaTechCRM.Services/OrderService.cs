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

        // Decouple from the HTTP request CT once the order is in the DB. Propagating the
        // request CT caused Medium/High-risk orders (≥$500, slower fraud path) to be
        // permanently stuck in FraudCheckPending whenever a client disconnect or LB reset
        // cancelled the token mid-flight — silently swallowed at the Kestrel level.
        // Use a dedicated timeout token so a hung fraud API cannot block threads forever.
        using var fraudCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var fraudResult = await _fraudShield.CheckAsync(order, fraudCts.Token);

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
