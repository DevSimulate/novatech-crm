using NovaTechCRM.Domain.Models;

namespace NovaTechCRM.Tests.Builders;

// Fluent test-data builders — keeps test setup readable and avoids copy-paste noise.
// Not a full fixture library; just the entities we construct repeatedly.

public class CustomerBuilder
{
    private int    _id    = 1;
    private string _name  = "Acme Corp";
    private string _email = "billing@acme.com";
    private CustomerTier   _tier   = CustomerTier.Standard;
    private CustomerStatus _status = CustomerStatus.Active;
    private decimal _monthlySpend  = 0m;
    private decimal _totalSpend    = 0m;

    public CustomerBuilder WithId(int id)             { _id    = id;    return this; }
    public CustomerBuilder WithName(string name)      { _name  = name;  return this; }
    public CustomerBuilder WithEmail(string email)    { _email = email; return this; }
    public CustomerBuilder WithTier(CustomerTier t)   { _tier  = t;     return this; }
    public CustomerBuilder WithStatus(CustomerStatus s){ _status = s;   return this; }
    public CustomerBuilder WithMonthlySpend(decimal v){ _monthlySpend = v; return this; }
    public CustomerBuilder WithTotalSpend(decimal v)  { _totalSpend   = v; return this; }

    public Customer Build() => new()
    {
        Id           = _id,
        Name         = _name,
        Email        = _email,
        Tier         = _tier,
        Status       = _status,
        MonthlySpend = _monthlySpend,
        TotalSpend   = _totalSpend,
        CreatedAt    = DateTime.UtcNow.AddMonths(-6),
    };
}

public class InvoiceBuilder
{
    private Guid   _id           = Guid.NewGuid();
    private int    _customerId   = 1;
    private string _number       = "INV-2024-00001";
    private InvoiceStatus _status = InvoiceStatus.Issued;
    private decimal _total       = 500m;
    private decimal _amountPaid  = 0m;
    private DateTime _dueAt      = DateTime.UtcNow.AddDays(30);

    public InvoiceBuilder WithId(Guid id)               { _id         = id;     return this; }
    public InvoiceBuilder WithCustomerId(int id)         { _customerId = id;     return this; }
    public InvoiceBuilder WithNumber(string n)           { _number     = n;      return this; }
    public InvoiceBuilder WithStatus(InvoiceStatus s)    { _status     = s;      return this; }
    public InvoiceBuilder WithTotal(decimal t)           { _total      = t;      return this; }
    public InvoiceBuilder WithAmountPaid(decimal a)      { _amountPaid = a;      return this; }
    public InvoiceBuilder WithDueAt(DateTime d)          { _dueAt = d;           return this; }
    public InvoiceBuilder Overdue()                      { _dueAt = DateTime.UtcNow.AddDays(-10); return this; }

    public Invoice Build() => new()
    {
        Id            = _id,
        CustomerId    = _customerId,
        CustomerName  = "Acme Corp",
        CustomerEmail = "billing@acme.com",
        InvoiceNumber = _number,
        Status        = _status,
        TotalAmount   = _total,
        SubTotal      = _total,
        AmountPaid    = _amountPaid,
        Currency      = "USD",
        PaymentTerms  = "NET30",
        IssuedAt      = DateTime.UtcNow.AddDays(-10),
        DueAt         = _dueAt,
        LineItems     = new List<InvoiceLineItem>
        {
            new()
            {
                Id          = Guid.NewGuid(),
                InvoiceId   = _id,
                Description = "Professional Services",
                Quantity    = 1,
                UnitPrice   = _total,
                Total       = _total,
            }
        },
        PaymentRecords = new List<InvoicePaymentRecord>(),
    };
}

public class PaymentBuilder
{
    private Guid   _id         = Guid.NewGuid();
    private int    _customerId = 1;
    private decimal _amount    = 250m;
    private PaymentStatus   _status   = PaymentStatus.Succeeded;
    private PaymentProvider _provider = PaymentProvider.Stripe;

    public PaymentBuilder WithId(Guid id)               { _id         = id;     return this; }
    public PaymentBuilder WithCustomerId(int id)         { _customerId = id;     return this; }
    public PaymentBuilder WithAmount(decimal a)          { _amount     = a;      return this; }
    public PaymentBuilder WithStatus(PaymentStatus s)    { _status     = s;      return this; }
    public PaymentBuilder WithProvider(PaymentProvider p){ _provider   = p;      return this; }

    public Payment Build() => new()
    {
        Id              = _id,
        CustomerId      = _customerId,
        Amount          = _amount,
        Currency        = "USD",
        Status          = _status,
        Provider        = _provider,
        Type            = PaymentType.Charge,
        CardLast4       = "4242",
        CardBrand       = "Visa",
        ProcessedAt     = DateTime.UtcNow,
        CreatedAt       = DateTime.UtcNow,
        ProviderPaymentId = "pi_test_" + Guid.NewGuid().ToString("N")[..12],
        ProviderChargeId  = "ch_test_" + Guid.NewGuid().ToString("N")[..12],
    };
}

public class DiscountBuilder
{
    private Guid   _id        = Guid.NewGuid();
    private string _code      = "SAVE10";
    private DiscountType _type = DiscountType.Percentage;
    private decimal _amount   = 10m;
    private bool _isStackable  = true;
    private int  _priority     = 1;
    private DiscountConflictResolution _conflict = DiscountConflictResolution.Additive;

    public DiscountBuilder WithCode(string c)               { _code     = c; return this; }
    public DiscountBuilder WithType(DiscountType t)         { _type     = t; return this; }
    public DiscountBuilder WithAmount(decimal a)            { _amount   = a; return this; }
    public DiscountBuilder Stackable(bool v = true)         { _isStackable = v; return this; }
    public DiscountBuilder WithPriority(int p)              { _priority = p; return this; }
    public DiscountBuilder WithConflict(DiscountConflictResolution c) { _conflict = c; return this; }

    public Discount Build() => new()
    {
        Id                 = _id,
        Code               = _code,
        Type               = _type,
        Amount             = _amount,
        IsStackable        = _isStackable,
        Priority           = _priority,
        ConflictResolution = _conflict,
        IsActive           = true,
        StartsAt           = null,
        ExpiresAt          = null,
        UsageCount         = 0,
    };
}

public static class Orders
{
    public static Order Simple(string customerId = "1") => new()
    {
        Id          = Guid.NewGuid(),
        CustomerId  = customerId,
        Status      = OrderStatus.Confirmed,
        TotalAmount = 300m,
        CreatedAt   = DateTime.UtcNow,
        Items       = new List<OrderItem>
        {
            new() { ProductSku = "SKU-001", ProductName = "Widget", Quantity = 3, UnitPrice = 100m }
        }
    };
}
