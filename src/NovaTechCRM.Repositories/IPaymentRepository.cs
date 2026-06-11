using NovaTechCRM.Domain.Models;

namespace NovaTechCRM.Repositories;

public interface IPaymentRepository
{
    Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Payment>> GetByCustomerAsync(int customerId, CancellationToken ct = default);
    Task<IReadOnlyList<Payment>> GetByInvoiceAsync(Guid invoiceId, CancellationToken ct = default);
    Task<Payment> CreateAsync(Payment payment, CancellationToken ct = default);
    Task<Payment> UpdateAsync(Payment payment, CancellationToken ct = default);
    Task AddRefundAsync(PaymentRefund refund, CancellationToken ct = default);
    Task<PaymentMethod?> GetPaymentMethodByIdAsync(Guid paymentMethodId, CancellationToken ct = default);
    Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(int customerId, CancellationToken ct = default);
    Task<PaymentMethod> CreatePaymentMethodAsync(PaymentMethod method, CancellationToken ct = default);
    Task<PaymentMethod> UpdatePaymentMethodAsync(PaymentMethod method, CancellationToken ct = default);
    Task DeletePaymentMethodAsync(Guid paymentMethodId, CancellationToken ct = default);
}
