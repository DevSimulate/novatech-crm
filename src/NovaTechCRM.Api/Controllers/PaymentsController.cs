using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Services.Interfaces;

namespace NovaTechCRM.Api.Controllers;

[Route("api/payments")]
[Authorize]
public class PaymentsController : BaseController
{
    private readonly IPaymentService _payments;

    public PaymentsController(IPaymentService payments) => _payments = payments;

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var payment = await _payments.GetByIdAsync(id, ct);
        if (payment is null) return NotFound();

        if (!IsAdmin && payment.CustomerId != CurrentCustomerId)
            return Forbid();

        return Ok(payment);
    }

    [HttpGet("customer/{customerId:int}")]
    public async Task<IActionResult> GetByCustomer(int customerId, CancellationToken ct)
    {
        if (!IsAdmin && CurrentCustomerId != customerId)
            return Forbid();

        var payments = await _payments.GetByCustomerAsync(customerId, ct);
        return Ok(payments);
    }

    [HttpPost("charge")]
    public async Task<IActionResult> Charge(
        [FromBody] ChargeRequest req, CancellationToken ct)
    {
        if (!IsAdmin && CurrentCustomerId != req.CustomerId)
            return Forbid();

        var payment = await _payments.ChargeAsync(
            req.CustomerId,
            req.Amount,
            req.Currency ?? "USD",
            req.InvoiceId,
            req.Provider,
            req.PaymentMethodId,
            ct);

        return Ok(payment);
    }

    [HttpPost("{id:guid}/refund")]
    [Authorize(Roles = "admin,manager")]
    public async Task<IActionResult> Refund(
        Guid id, [FromBody] RefundRequest req, CancellationToken ct)
    {
        var refund = await _payments.RefundAsync(id, req.Amount, req.Reason, CurrentUserId, ct);
        return Ok(refund);
    }

    // ── Payment Methods ──────────────────────────────────────────────────────

    [HttpGet("methods/customer/{customerId:int}")]
    public async Task<IActionResult> GetMethods(int customerId, CancellationToken ct)
    {
        if (!IsAdmin && CurrentCustomerId != customerId)
            return Forbid();

        var methods = await _payments.GetPaymentMethodsAsync(customerId, ct);
        return Ok(methods);
    }

    [HttpPost("methods")]
    public async Task<IActionResult> SaveMethod(
        [FromBody] SavePaymentMethodRequest req, CancellationToken ct)
    {
        if (!IsAdmin && CurrentCustomerId != req.CustomerId)
            return Forbid();

        var method = new PaymentMethod
        {
            Type          = req.Type,
            Token         = req.Token,
            CardLast4     = req.CardLast4,
            CardBrand     = req.CardBrand,
            ExpiryMonth   = req.ExpiryMonth,
            ExpiryYear    = req.ExpiryYear,
            BillingName   = req.BillingName,
            IsDefault     = req.IsDefault,
        };

        var saved = await _payments.SavePaymentMethodAsync(req.CustomerId, method, ct);
        return Created($"/api/payments/methods/{saved.Id}", saved);
    }

    // NOVA-83: IDOR vulnerability — fetches payment method by ID without checking
    // that the method belongs to the currently authenticated customer.
    // An attacker who knows (or guesses) a payment method GUID can retrieve another
    // customer's saved card details: last4, brand, billing name, expiry.
    // GUIDs are not secrets — they appear in webhook payloads, logs, and can be
    // enumerated if an attacker has any legitimate access to the system.
    //
    // Fix: load the method, then assert method.CustomerId == CurrentCustomerId (or IsAdmin).
    [HttpGet("methods/{paymentMethodId:guid}")]
    public async Task<IActionResult> GetMethod(Guid paymentMethodId, CancellationToken ct)
    {
        var methods = await _payments.GetPaymentMethodsAsync(
            // BUG: uses CurrentCustomerId to look up methods but then returns by ID
            // without re-checking ownership — if the caller supplies a GUID that belongs
            // to a different customer, GetPaymentMethodByIdAsync would return it.
            // Currently this calls GetPaymentMethodsAsync which filters by customer,
            // but the direct-by-ID path added in a later PR does not.
            CurrentCustomerId, ct);

        // this works, but the overload added for the mobile app bypasses this filter:
        // var method = await _payments.GetPaymentMethodByIdAsync(paymentMethodId, ct);
        // return method is null ? NotFound() : Ok(method);   // <-- no ownership check

        var method = methods.FirstOrDefault(m => m.Id == paymentMethodId);
        return method is null ? NotFound() : Ok(method);
    }

    // The "mobile app" overload planted below — this is the actual vulnerable endpoint
    // that was added in a rush for the mobile team and missed the ownership check.
    [HttpGet("methods/{paymentMethodId:guid}/details")]
    public async Task<IActionResult> GetMethodDetails(Guid paymentMethodId, CancellationToken ct)
    {
        // NOVA-83 BUG: fetches by ID with no ownership check.
        // Any authenticated user can GET /api/payments/methods/{anyGuid}/details
        // and receive full card metadata for any customer in the system.
        var allMethods = await _payments.GetPaymentMethodsAsync(0, ct); // 0 fetches ALL customers
        var method     = allMethods.FirstOrDefault(m => m.Id == paymentMethodId);
        return method is null ? NotFound() : Ok(method);
    }

    [HttpDelete("methods/{paymentMethodId:guid}")]
    public async Task<IActionResult> DeleteMethod(Guid paymentMethodId, CancellationToken ct)
    {
        var method = await _payments.GetPaymentMethodByIdAsync(paymentMethodId, ct);
        if (method is null) return NotFound();

        if (!IsAdmin && method.CustomerId != CurrentCustomerId)
            return Forbid();

        await _payments.DeletePaymentMethodAsync(paymentMethodId, ct);
        return NoContent();
    }
}

public record ChargeRequest(
    int CustomerId,
    decimal Amount,
    string? Currency,
    Guid? InvoiceId,
    PaymentProvider Provider,
    Guid? PaymentMethodId
);

public record RefundRequest(decimal Amount, string Reason);

public record SavePaymentMethodRequest(
    int CustomerId,
    PaymentMethodType Type,
    string Token,
    string? CardLast4,
    string? CardBrand,
    int? ExpiryMonth,
    int? ExpiryYear,
    string? BillingName,
    bool IsDefault
);
