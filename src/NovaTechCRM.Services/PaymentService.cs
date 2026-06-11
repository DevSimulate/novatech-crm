using Microsoft.Extensions.Logging;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Domain.Exceptions;
using NovaTechCRM.Repositories;
using NovaTechCRM.Services.Interfaces;

namespace NovaTechCRM.Services;

public class PaymentService : IPaymentService
{
    private readonly IPaymentRepository _paymentRepo;
    private readonly IInvoiceService _invoiceService;
    private readonly INotificationService _notifications;
    private readonly IAuditService _audit;
    private readonly IPaymentProviderFactory _providerFactory;
    private readonly ILogger<PaymentService> _logger;

    public PaymentService(
        IPaymentRepository paymentRepo,
        IInvoiceService invoiceService,
        INotificationService notifications,
        IAuditService audit,
        IPaymentProviderFactory providerFactory,
        ILogger<PaymentService> logger)
    {
        _paymentRepo     = paymentRepo;
        _invoiceService  = invoiceService;
        _notifications   = notifications;
        _audit           = audit;
        _providerFactory = providerFactory;
        _logger          = logger;
    }

    public async Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _paymentRepo.GetByIdAsync(id, ct);

    public async Task<IReadOnlyList<Payment>> GetByCustomerAsync(
        int customerId, CancellationToken ct = default) =>
        await _paymentRepo.GetByCustomerAsync(customerId, ct);

    public async Task<IReadOnlyList<Payment>> GetByInvoiceAsync(
        Guid invoiceId, CancellationToken ct = default) =>
        await _paymentRepo.GetByInvoiceAsync(invoiceId, ct);

    public async Task<Payment> ChargeAsync(
        int customerId,
        decimal amount,
        string currency,
        Guid? invoiceId,
        PaymentProvider provider,
        Guid? paymentMethodId,
        CancellationToken ct = default)
    {
        if (amount <= 0)
            throw new PaymentFailedException("Charge amount must be greater than zero.");

        var payment = new Payment
        {
            CustomerId      = customerId,
            InvoiceId       = invoiceId,
            Amount          = amount,
            Currency        = currency,
            Provider        = provider,
            Type            = PaymentType.Charge,
            Status          = PaymentStatus.Processing,
            PaymentMethodId = paymentMethodId,
            CreatedAt       = DateTime.UtcNow
        };

        var created = await _paymentRepo.CreateAsync(payment, ct);

        try
        {
            var providerImpl = _providerFactory.GetProvider(provider);
            var result       = await providerImpl.ChargeAsync(created, ct);

            created.Status            = result.Success ? PaymentStatus.Succeeded : PaymentStatus.Failed;
            created.ProviderPaymentId = result.ProviderPaymentId;
            created.ProviderChargeId  = result.ProviderChargeId;
            created.CardLast4         = result.CardLast4;
            created.CardBrand         = result.CardBrand;
            created.ProcessedAt       = DateTime.UtcNow;

            if (!result.Success)
            {
                created.FailureCode    = result.ErrorCode;
                created.FailureMessage = result.ErrorMessage;
                created.FailedAt       = DateTime.UtcNow;
            }

            await _paymentRepo.UpdateAsync(created, ct);

            if (result.Success && invoiceId.HasValue)
                await _invoiceService.RecordPaymentAsync(invoiceId.Value, amount, created.Id, ct);

            if (result.Success)
                await _notifications.SendPaymentConfirmationAsync(created, ct);
            else
                _logger.LogWarning("Payment {PaymentId} failed: {Code} - {Message}",
                    created.Id, result.ErrorCode, result.ErrorMessage);

            await _audit.LogAsync(AuditAction.Created, "Payment", created.Id.ToString(),
                null, newValues: new { created.Amount, created.Status, created.Provider }, ct: ct);

            return created;
        }
        catch (Exception ex)
        {
            created.Status    = PaymentStatus.Failed;
            created.FailedAt  = DateTime.UtcNow;
            created.FailureMessage = ex.Message;
            await _paymentRepo.UpdateAsync(created, ct);

            _logger.LogError(ex, "Unhandled exception charging payment {PaymentId}", created.Id);
            throw new PaymentFailedException($"Payment processing failed: {ex.Message}");
        }
    }

    public async Task<Payment> RefundAsync(
        Guid paymentId, decimal amount, string reason, string userId,
        CancellationToken ct = default)
    {
        var payment = await _paymentRepo.GetByIdAsync(paymentId, ct)
            ?? throw new PaymentFailedException($"Payment {paymentId} not found.");

        if (payment.Status != PaymentStatus.Succeeded)
            throw new PaymentFailedException("Can only refund succeeded payments.");

        var maxRefundable = payment.Amount - (payment.RefundedAmount ?? 0);
        if (amount > maxRefundable)
            throw new PaymentFailedException(
                $"Refund amount {amount:C} exceeds refundable amount {maxRefundable:C}.");

        var providerImpl = _providerFactory.GetProvider(payment.Provider);
        var result       = await providerImpl.RefundAsync(payment, amount, ct);

        if (!result.Success)
            throw new PaymentFailedException(result.ErrorMessage ?? "Refund failed.");

        var refund = new PaymentRefund
        {
            PaymentId          = paymentId,
            Amount             = amount,
            Reason             = reason,
            ProviderRefundId   = result.ProviderRefundId,
            Status             = PaymentStatus.Succeeded,
            ProcessedAt        = DateTime.UtcNow,
            InitiatedByUserId  = userId
        };

        await _paymentRepo.AddRefundAsync(refund, ct);

        payment.RefundedAmount = (payment.RefundedAmount ?? 0) + amount;
        payment.Status = payment.IsFullyRefunded ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
        await _paymentRepo.UpdateAsync(payment, ct);

        await _audit.LogAsync(AuditAction.Updated, "Payment", paymentId.ToString(), userId,
            oldValues: new { Status = PaymentStatus.Succeeded },
            newValues: new { payment.Status, RefundAmount = amount }, ct: ct);

        _logger.LogInformation("Refund {Amount:C} processed for payment {PaymentId} by {User}",
            amount, paymentId, userId);

        return payment;
    }

    public async Task<PaymentMethod?> GetPaymentMethodByIdAsync(
        Guid paymentMethodId, CancellationToken ct = default) =>
        await _paymentRepo.GetPaymentMethodByIdAsync(paymentMethodId, ct);

    public async Task<PaymentMethod> SavePaymentMethodAsync(
        int customerId, PaymentMethod method, CancellationToken ct = default)
    {
        method.CustomerId = customerId;
        method.CreatedAt  = DateTime.UtcNow;

        // if set as default, clear others
        if (method.IsDefault)
        {
            var existing = await _paymentRepo.GetPaymentMethodsAsync(customerId, ct);
            foreach (var m in existing.Where(m => m.IsDefault))
            {
                m.IsDefault = false;
                await _paymentRepo.UpdatePaymentMethodAsync(m, ct);
            }
        }

        return await _paymentRepo.CreatePaymentMethodAsync(method, ct);
    }

    public async Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(
        int customerId, CancellationToken ct = default) =>
        await _paymentRepo.GetPaymentMethodsAsync(customerId, ct);

    public async Task DeletePaymentMethodAsync(Guid paymentMethodId, CancellationToken ct = default)
    {
        await _paymentRepo.DeletePaymentMethodAsync(paymentMethodId, ct);
    }

    public async Task HandleWebhookAsync(
        PaymentProvider provider, string payload, string signature,
        CancellationToken ct = default)
    {
        _logger.LogDebug("Received {Provider} payment webhook", provider);

        var providerImpl = _providerFactory.GetProvider(provider);
        await providerImpl.HandleWebhookAsync(payload, signature, ct);
    }
}

// marker interfaces — implementations in Infrastructure
public interface IPaymentProviderFactory
{
    IPaymentProvider GetProvider(PaymentProvider provider);
}

public interface IPaymentProvider
{
    Task<PaymentProviderResult> ChargeAsync(Payment payment, CancellationToken ct);
    Task<PaymentProviderResult> RefundAsync(Payment payment, decimal amount, CancellationToken ct);
    Task HandleWebhookAsync(string payload, string signature, CancellationToken ct);
}

public record PaymentProviderResult(
    bool Success,
    string? ProviderPaymentId = null,
    string? ProviderChargeId  = null,
    string? ProviderRefundId  = null,
    string? CardLast4         = null,
    string? CardBrand         = null,
    string? ErrorCode         = null,
    string? ErrorMessage      = null
);
