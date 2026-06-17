using Microsoft.EntityFrameworkCore;
using NovaTechCRM.Domain.Models;

namespace NovaTechCRM.Repositories;

public class PaymentRepository : IPaymentRepository
{
    private readonly NovaTechDbContext _db;

    public PaymentRepository(NovaTechDbContext db) => _db = db;

    public async Task<Payment?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.Payments
            .Include(p => p.Refunds)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<Payment>> GetByCustomerAsync(
        int customerId, CancellationToken ct = default)
        => await _db.Payments
            .AsNoTracking()
            .Where(p => p.CustomerId == customerId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Payment>> GetByInvoiceAsync(
        Guid invoiceId, CancellationToken ct = default)
        => await _db.Payments
            .AsNoTracking()
            .Where(p => p.InvoiceId == invoiceId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

    public async Task<Payment> CreateAsync(Payment payment, CancellationToken ct = default)
    {
        _db.Payments.Add(payment);
        await _db.SaveChangesAsync(ct);
        return payment;
    }

    public async Task<Payment> UpdateAsync(Payment payment, CancellationToken ct = default)
    {
        _db.Payments.Update(payment);
        await _db.SaveChangesAsync(ct);
        return payment;
    }

    public async Task AddRefundAsync(PaymentRefund refund, CancellationToken ct = default)
    {
        _db.PaymentRefunds.Add(refund);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<PaymentMethod?> GetPaymentMethodByIdAsync(
        Guid paymentMethodId, CancellationToken ct = default)
        => await _db.PaymentMethods
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == paymentMethodId && !m.IsDeleted, ct);

    public async Task<IReadOnlyList<PaymentMethod>> GetPaymentMethodsAsync(
        int customerId, CancellationToken ct = default)
        => await _db.PaymentMethods
            .AsNoTracking()
            .Where(m => m.CustomerId == customerId && !m.IsDeleted)
            .OrderByDescending(m => m.IsDefault)
            .ThenByDescending(m => m.CreatedAt)
            .ToListAsync(ct);

    public async Task<PaymentMethod> CreatePaymentMethodAsync(
        PaymentMethod method, CancellationToken ct = default)
    {
        _db.PaymentMethods.Add(method);
        await _db.SaveChangesAsync(ct);
        return method;
    }

    public async Task<PaymentMethod> UpdatePaymentMethodAsync(
        PaymentMethod method, CancellationToken ct = default)
    {
        _db.PaymentMethods.Update(method);
        await _db.SaveChangesAsync(ct);
        return method;
    }

    public async Task DeletePaymentMethodAsync(
        Guid paymentMethodId, CancellationToken ct = default)
    {
        // soft delete — payment methods are referenced by past payments
        await _db.PaymentMethods
            .Where(m => m.Id == paymentMethodId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsDeleted, true), ct);
    }
}
