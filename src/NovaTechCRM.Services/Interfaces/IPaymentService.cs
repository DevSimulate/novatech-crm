using NovaTechCRM.Domain.Models;

namespace NovaTechCRM.Services.Interfaces;

public interface IPaymentService
{
    Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Payment>> GetByCustomerAsync(int customerId, CancellationToken ct = default);
    Task<IReadOnlyList<Payment>> GetByInvoiceAsync(Guid invoiceId, CancellationToken ct = default);
    Task<Payment> ChargeAsync(int customerId, decimal amount, string currency, Guid? invoiceId, PaymentProvider provider, Guid? paymentMethodId, CancellationToken ct = default);
    Task<Payment> RefundAsync(Guid paymentId, decimal amount, string reason, string userId, CancellationToken ct = default);
    Task<PaymentMethod?> GetPaymentMethodByIdAsync(Guid paymentMethodId, CancellationToken ct = default);
    Task<PaymentMethod> SavePaymentMethodAsync(int customerId, PaymentMethod method, CancellationToken ct = default);
    Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(int customerId, CancellationToken ct = default);
    Task DeletePaymentMethodAsync(Guid paymentMethodId, CancellationToken ct = default);
    Task HandleWebhookAsync(PaymentProvider provider, string payload, string signature, CancellationToken ct = default);
}
