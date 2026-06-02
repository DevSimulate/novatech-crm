using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Repositories;
using NovaTechCRM.Services;
using Xunit;

namespace NovaTechCRM.Tests;

public class OrderServiceTests
{
    private readonly Mock<IOrderRepository> _repoMock = new();
    private readonly Mock<IFraudShieldService> _fraudMock = new();
    private readonly Mock<INotificationService> _notifMock = new();

    private OrderService CreateSut() => new(
        _repoMock.Object,
        _fraudMock.Object,
        _notifMock.Object,
        NullLogger<OrderService>.Instance);

    [Fact]
    public async Task PlaceOrder_HighRiskOrder_ShouldNotFulfill()
    {
        // Arrange
        var order = new Order
        {
            CustomerId = "cust-001",
            TotalAmount = 9999m,
            Items = new List<OrderItem>
            {
                new() { ProductSku = "SKU-X", ProductName = "Expensive Item", Quantity = 1, UnitPrice = 9999m }
            }
        };

        _fraudMock
            .Setup(f => f.CheckAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FraudCheckResult
            {
                CheckId = "chk-001",
                Passed = false,
                RiskLevel = FraudRiskLevel.Critical,
                Reason = "Amount exceeds threshold"
            });

        _repoMock
            .Setup(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        // Act
        var result = await sut.PlaceOrderAsync(order);

        Assert.NotEqual(OrderStatus.Fulfilled, result.Status);
        Assert.Equal(OrderStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task PlaceOrder_LowRiskOrder_ShouldFulfill()
    {
        var order = new Order
        {
            CustomerId = "cust-002",
            TotalAmount = 49.99m,
            Items = new List<OrderItem>
            {
                new() { ProductSku = "SKU-A", ProductName = "Widget", Quantity = 1, UnitPrice = 49.99m }
            }
        };

        _fraudMock
            .Setup(f => f.CheckAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FraudCheckResult
            {
                CheckId = "chk-002",
                Passed = true,
                RiskLevel = FraudRiskLevel.Low,
                Reason = "Automated check passed"
            });

        _repoMock
            .Setup(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _notifMock
            .Setup(n => n.SendOrderConfirmationAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        var result = await sut.PlaceOrderAsync(order);

        Assert.Equal(OrderStatus.Fulfilled, result.Status);
    }

    [Fact]
    public async Task PlaceOrder_ClientDisconnectDuringFraudCheck_DoesNotLeaveOrderStuck()
    {
        // Arrange — pre-cancelled token simulates a client disconnect or LB reset
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var order = new Order
        {
            CustomerId = "cust-003",
            TotalAmount = 750m,
            Items = new List<OrderItem>
            {
                new() { ProductSku = "SKU-B", ProductName = "Mid-range Item", Quantity = 1, UnitPrice = 750m }
            }
        };

        // Fraud check accepts any token — internally the service creates a 30s timeout token,
        // not the HTTP request CT, so this mock will be called regardless of the cancelled ct.
        _fraudMock
            .Setup(f => f.CheckAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FraudCheckResult
            {
                CheckId = "chk-003",
                Passed = true,
                RiskLevel = FraudRiskLevel.Medium,
                Reason = "Automated check passed"
            });

        _repoMock
            .Setup(r => r.SaveAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _notifMock
            .Setup(n => n.SendOrderConfirmationAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = CreateSut();

        // Act — fulfillment must complete even with a pre-cancelled HTTP request token
        var result = await sut.PlaceOrderAsync(order, cts.Token);

        Assert.Equal(OrderStatus.Fulfilled, result.Status);
    }
}
