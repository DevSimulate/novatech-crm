using NovaTechCRM.Domain.ValueObjects;

namespace NovaTechCRM.Domain.Models;

// Invoice status flow:
// Draft -> Issued -> PartiallyPaid -> Paid -> Voided
//                 -> Overdue (background job sets this)
//                 -> Disputed
public enum InvoiceStatus
{
    Draft           = 0,
    Issued          = 1,
    Sent            = 2,
    PartiallyPaid   = 3,
    Paid            = 4,
    Overdue         = 5,
    Disputed        = 6,
    Voided          = 7,
    WrittenOff      = 8
}

public class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();

    // human-readable number e.g. INV-2024-00142
    public string InvoiceNumber { get; set; } = string.Empty;

    // TODO: should be FK to Customer entity, currently duplicated string
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;

    // linked order — optional, some invoices are standalone
    public Guid? OrderId { get; set; }

    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;

    public List<InvoiceLineItem> LineItems { get; set; } = new();

    public decimal SubTotal { get; set; }
    public decimal TaxRate { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal DiscountAmount { get; set; }

    // NOVA-58: TotalAmount calculation doesn't account for stacked discounts
    // See DiscountService for the conflict
    public decimal TotalAmount { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal AmountDue => TotalAmount - AmountPaid;

    public string Currency { get; set; } = "USD";

    public DateTime IssuedAt { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime? VoidedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public string? Notes { get; set; }
    public string? InternalNotes { get; set; }
    public string? PdfUrl { get; set; }

    // Payment terms e.g. "NET30", "NET60", "DUE_ON_RECEIPT"
    public string PaymentTerms { get; set; } = "NET30";

    public string? BillingAddressLine1 { get; set; }
    public string? BillingAddressLine2 { get; set; }
    public string? BillingCity { get; set; }
    public string? BillingState { get; set; }
    public string? BillingPostalCode { get; set; }
    public string? BillingCountry { get; set; }

    // Tracks which user created/updated
    public string? CreatedByUserId { get; set; }
    public string? LastModifiedByUserId { get; set; }

    // for recurring invoices
    public bool IsRecurring { get; set; }
    public string? RecurrencePattern { get; set; } // "MONTHLY", "QUARTERLY" etc
    public Guid? ParentInvoiceId { get; set; }

    // Contract requires 3 business days grace before an overdue notice may be sent.
    public bool IsOverdue => Status == InvoiceStatus.Issued
                             && BusinessDays.Add(DueAt, BusinessDays.OverdueGracePeriod) < DateTime.UtcNow
                             && AmountDue > 0;

    // void an invoice — doesn't delete, creates audit trail
    public void Void(string reason)
    {
        if (Status == InvoiceStatus.Paid)
            throw new InvalidOperationException("Cannot void a fully paid invoice. Issue a credit note instead.");

        Status = InvoiceStatus.Voided;
        VoidedAt = DateTime.UtcNow;
        InternalNotes = $"[VOIDED {DateTime.UtcNow:u}] {reason}\n{InternalNotes}";
    }

    public void RecalculateTotals()
    {
        SubTotal = LineItems.Sum(li => li.LineTotal);
        TaxAmount = Math.Round(SubTotal * TaxRate, 2);
        TotalAmount = SubTotal + TaxAmount - DiscountAmount;
    }
}

public class InvoiceLineItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }

    public string Description { get; set; } = string.Empty;
    public string? ProductSku { get; set; }

    public decimal UnitPrice { get; set; }
    public decimal Quantity { get; set; }  // decimal to allow hours/fractional units
    public decimal LineTotal => Math.Round(UnitPrice * Quantity, 2);

    public decimal? DiscountPercent { get; set; }
    public decimal? TaxRate { get; set; }

    // sort order within invoice
    public int SortOrder { get; set; }

    // period this line covers — used for subscription invoices
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
}

public class InvoicePaymentRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public Guid PaymentId { get; set; }
    public decimal AmountApplied { get; set; }
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
    public string AppliedByUserId { get; set; } = string.Empty;
}

// used only for the invoice list API response
// TODO: this duplicates InvoiceDto in the web layer — clean up
public class InvoiceSummary
{
    public Guid Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public decimal AmountDue { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime DueAt { get; set; }
    public bool IsOverdue { get; set; }
}
