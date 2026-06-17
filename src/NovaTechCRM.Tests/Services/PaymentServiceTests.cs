using Moq;
using Xunit;
using Microsoft.Extensions.Logging;
using NovaTechCRM.Domain.Exceptions;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Repositories;
using NovaTechCRM.Services;
using NovaTechCRM.Services.Interfaces;
using NovaTechCRM.Tests.Builders;

namespace NovaTechCRM.Tests.Services;

public class PaymentServiceTests
{
    private readonly Mock<IPaymentRepository>        _paymentRepo     = new();
    private readonly Mock<IInvoiceService>           _invoiceService  = new();
    private readonly Mock<INotificationService>      _notify          = new();
    private readonly Mock<IAuditService>             _audit           = new();
    private readonly Mock<IPaymentProviderFactory>   _providerFactory = new();
    private readonly Mock<IPaymentProvider>          _provider        = new();
    private readonly Mock<ILogger<PaymentService>>   _logger          = new();

    public PaymentServiceTests()
    {
        _providerFactory
            .Setup(f => f.GetProvider(It.IsAny<PaymentProvider>()))
            .Returns(_provider.Object);
    }

    private PaymentService CreateSut() => new(
        _paymentRepo.Object, _invoiceService.Object, _notify.Object,
        _audit.Object, _providerFactory.Object, _logger.Object);

    [Fact]
    public async Task ChargeAsync_ReturnsSucceededPayment_OnProviderSuccess()
    {
        _paymentRepo
            .Setup(r => r.CreateAsync(It.IsAny<Payment>(), default))
            .ReturnsAsync((Payment p, CancellationToken _) => p);
        _paymentRepo
            .Setup(r => r.UpdateAsync(It.IsAny<Payment>(), default))
            .ReturnsAsync((Payment p, CancellationToken _) => p);

        _provider.Setup(p => p.ChargeAsync(It.IsAny<Payment>(), default))
            .ReturnsAsync(new PaymentProviderResult(
                Success: true,
                ProviderPaymentId: "pi_123",
                ProviderChargeId:  "ch_123",
                CardLast4:         "4242",
                CardBrand:         "Visa"));

        var result = await CreateSut().ChargeAsync(1, 100m, "USD", null, PaymentProvider.Stripe, null);

        Assert.Equal(PaymentStatus.Succeeded, result.Status);
        Assert.Equal("4242", result.CardLast4);
    }

    [Fact]
    public async Task ChargeAsync_ReturnsFailedPayment_OnProviderDecline()
    {
        _paymentRepo.Setup(r => r.CreateAsync(It.IsAny<Payment>(), default))
            .ReturnsAsync((Payment p, CancellationToken _) => p);
        _paymentRepo.Setup(r => r.UpdateAsync(It.IsAny<Payment>(), default))
            .ReturnsAsync((Payment p, CancellationToken _) => p);

        _provider.Setup(p => p.ChargeAsync(It.IsAny<Payment>(), default))
            .ReturnsAsync(new PaymentProviderResult(
                Success:      false,
                ErrorCode:    "card_declined",
                ErrorMessage: "Your card was declined."));

        var result = await CreateSut().ChargeAsync(1, 50m, "USD", null, PaymentProvider.Stripe, null);

        Assert.Equal(PaymentStatus.Failed, result.Status);
        Assert.Equal("card_declined", result.FailureCode);
    }

    [Fact]
    public async Task ChargeAsync_Throws_WhenAmountIsZero()
    {
        await Assert.ThrowsAsync<PaymentFailedException>(
            () => CreateSut().ChargeAsync(1, 0m, "USD", null, PaymentProvider.Stripe, null));
    }

    [Fact]
    public async Task ChargeAsync_Throws_WhenAmountIsNegative()
    {
        await Assert.ThrowsAsync<PaymentFailedException>(
            () => CreateSut().ChargeAsync(1, -10m, "USD", null, PaymentProvider.Stripe, null));
    }

    [Fact]
    public async Task ChargeAsync_RecordsInvoicePayment_WhenSuccessAndInvoiceIdProvided()
    {
        var invoiceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        _paymentRepo.Setup(r => r.CreateAsync(It.IsAny<Payment>(), default))
            .ReturnsAsync((Payment p, CancellationToken _) => { p.Id = paymentId; return p; });
        _paymentRepo.Setup(r => r.UpdateAsync(It.IsAny<Payment>(), default))
            .ReturnsAsync((Payment p, CancellationToken _) => p);

        _provider.Setup(p => p.ChargeAsync(It.IsAny<Payment>(), default))
            .ReturnsAsync(new PaymentProviderResult(Success: true, ProviderPaymentId: "pi_x"));

        await CreateSut().ChargeAsync(1, 200m, "USD", invoiceId, PaymentProvider.Stripe, null);

        _invoiceService.Verify(
            s => s.RecordPaymentAsync(invoiceId, 200m, It.IsAny<Guid>(), default),
            Times.Once);
    }

    [Fact]
    public async Task RefundAsync_Throws_WhenPaymentNotSucceeded()
    {
        var payment = new PaymentBuilder()
            .WithStatus(PaymentStatus.Failed)
            .Build();

        _paymentRepo.Setup(r => r.GetByIdAsync(payment.Id, default)).ReturnsAsync(payment);

        await Assert.ThrowsAsync<PaymentFailedException>(
            () => CreateSut().RefundAsync(payment.Id, 50m, "Test refund", "admin"));
    }

    [Fact]
    public async Task RefundAsync_Throws_WhenRefundExceedsRemaining()
    {
        var payment = new PaymentBuilder()
            .WithAmount(100m)
            .WithStatus(PaymentStatus.Succeeded)
            .Build();
        payment.RefundedAmount = 80m;

        _paymentRepo.Setup(r => r.GetByIdAsync(payment.Id, default)).ReturnsAsync(payment);

        // trying to refund 30 when only 20 remains
        await Assert.ThrowsAsync<PaymentFailedException>(
            () => CreateSut().RefundAsync(payment.Id, 30m, "reason", "admin"));
    }

    [Fact]
    public async Task RefundAsync_SetsRefundedStatus_WhenFullyRefunded()
    {
        var payment = new PaymentBuilder()
            .WithAmount(100m)
            .WithStatus(PaymentStatus.Succeeded)
            .Build();
        payment.RefundedAmount = 0;

        _paymentRepo.Setup(r => r.GetByIdAsync(payment.Id, default)).ReturnsAsync(payment);
        _paymentRepo.Setup(r => r.AddRefundAsync(It.IsAny<PaymentRefund>(), default))
            .Returns(Task.CompletedTask);
        _paymentRepo.Setup(r => r.UpdateAsync(It.IsAny<Payment>(), default))
            .ReturnsAsync((Payment p, CancellationToken _) => p);

        _provider.Setup(p => p.RefundAsync(It.IsAny<Payment>(), It.IsAny<decimal>(), default))
            .ReturnsAsync(new PaymentProviderResult(Success: true, ProviderRefundId: "re_123"));

        var result = await CreateSut().RefundAsync(payment.Id, 100m, "full refund", "admin");

        Assert.Equal(PaymentStatus.Refunded, result.Status);
    }

    [Fact]
    public async Task SavePaymentMethodAsync_ClearsOtherDefaults_WhenSetAsDefault()
    {
        var existingDefault = new PaymentMethod
        {
            Id         = Guid.NewGuid(),
            CustomerId = 1,
            IsDefault  = true,
        };

        _paymentRepo.Setup(r => r.GetPaymentMethodsAsync(1, default))
            .ReturnsAsync(new List<PaymentMethod> { existingDefault });
        _paymentRepo.Setup(r => r.UpdatePaymentMethodAsync(It.IsAny<PaymentMethod>(), default))
            .ReturnsAsync((PaymentMethod m, CancellationToken _) => m);
        _paymentRepo.Setup(r => r.CreatePaymentMethodAsync(It.IsAny<PaymentMethod>(), default))
            .ReturnsAsync((PaymentMethod m, CancellationToken _) => m);

        var newMethod = new PaymentMethod { IsDefault = true };
        await CreateSut().SavePaymentMethodAsync(1, newMethod);

        _paymentRepo.Verify(
            r => r.UpdatePaymentMethodAsync(
                It.Is<PaymentMethod>(m => m.Id == existingDefault.Id && !m.IsDefault),
                default),
            Times.Once);
    }

    // NOVA-84: GetPaymentMethodByIdAsync is the read used by DeleteMethod to enforce ownership.
    // It must return null for a soft-deleted method so that a deleted card's ID cannot be
    // re-submitted and receive a Forbid (which would leak that the ID once existed).
    [Fact]
    public async Task GetPaymentMethodByIdAsync_ReturnsNull_WhenMethodNotFound()
    {
        var id = Guid.NewGuid();
        _paymentRepo.Setup(r => r.GetPaymentMethodByIdAsync(id, default))
            .ReturnsAsync((PaymentMethod?)null);

        var result = await CreateSut().GetPaymentMethodByIdAsync(id);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPaymentMethodByIdAsync_ReturnsMethod_WhenFound()
    {
        var method = new PaymentMethod { Id = Guid.NewGuid(), CustomerId = 7 };
        _paymentRepo.Setup(r => r.GetPaymentMethodByIdAsync(method.Id, default))
            .ReturnsAsync(method);

        var result = await CreateSut().GetPaymentMethodByIdAsync(method.Id);

        Assert.NotNull(result);
        Assert.Equal(7, result.CustomerId);
    }

    [Fact]
    public async Task ChargeAsync_SetsPaymentToFailed_WhenProviderThrows()
    {
        _paymentRepo.Setup(r => r.CreateAsync(It.IsAny<Payment>(), default))
            .ReturnsAsync((Payment p, CancellationToken _) => p);
        _paymentRepo.Setup(r => r.UpdateAsync(It.IsAny<Payment>(), default))
            .ReturnsAsync((Payment p, CancellationToken _) => p);

        _provider.Setup(p => p.ChargeAsync(It.IsAny<Payment>(), default))
            .ThrowsAsync(new HttpRequestException("Network error"));

        await Assert.ThrowsAsync<PaymentFailedException>(
            () => CreateSut().ChargeAsync(1, 75m, "USD", null, PaymentProvider.Stripe, null));

        _paymentRepo.Verify(
            r => r.UpdateAsync(It.Is<Payment>(p => p.Status == PaymentStatus.Failed), default),
            Times.Once);
    }
}
