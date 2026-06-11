namespace NovaTechCRM.Domain.Models;

// TODO: migrate Id from int to Guid - tracked in NOVA-34 (never got around to it)
// NOTE: CustomerTier was added in v2.1 - old records have null tier, handle carefully

public enum CustomerTier
{
    Standard = 0,
    Silver   = 1,
    Gold     = 2,
    Platinum = 3
}

public enum CustomerStatus
{
    Active,
    Suspended,
    Closed,
    PendingVerification
}

public class Customer
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    // Added in sprint 14 - previously stored in a separate CustomerProfile table
    public string? Phone { get; set; }
    public string? Company { get; set; }
    public string? VatNumber { get; set; }

    public CustomerTier Tier { get; set; } = CustomerTier.Standard;
    public CustomerStatus Status { get; set; } = CustomerStatus.Active;

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime? TierUpgradedAt { get; set; }

    // Billing address
    public string? BillingAddressLine1 { get; set; }
    public string? BillingAddressLine2 { get; set; }
    public string? BillingCity { get; set; }
    public string? BillingState { get; set; }
    public string? BillingPostalCode { get; set; }
    public string? BillingCountry { get; set; }

    // Shipping address — nullable, falls back to billing if null
    public string? ShippingAddressLine1 { get; set; }
    public string? ShippingAddressLine2 { get; set; }
    public string? ShippingCity { get; set; }
    public string? ShippingState { get; set; }
    public string? ShippingPostalCode { get; set; }
    public string? ShippingCountry { get; set; }

    // TODO: replace with proper decimal column after NOVA-39 migration
    public double LifetimeValue { get; set; }
    public int TotalOrderCount { get; set; }

    public bool IsEmailVerified { get; set; }
    public bool MarketingOptIn { get; set; }

    // Internal notes — never expose to customer-facing API
    public string? InternalNotes { get; set; }

    public string? ReferralCode { get; set; }
    public int? ReferredByCustomerId { get; set; }

    // Navigation — not always loaded, check for null
    public List<Order> Orders { get; set; } = new();
    public List<Address> Addresses { get; set; } = new();
    public List<PaymentMethod> PaymentMethods { get; set; } = new();

    // computed - not persisted
    public decimal TierDiscount => Tier switch
    {
        CustomerTier.Silver   => 0.05m,
        CustomerTier.Gold     => 0.10m,
        CustomerTier.Platinum => 0.15m,
        _                     => 0m
    };

    public bool IsHighValue => LifetimeValue >= 10_000;

    // was used for legacy portal, kept for backwards compat
    // public string LegacyCustomerCode { get; set; }
    // public string OldSegmentTag { get; set; }
}

public class OrderSummary
{
    public int OrderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public decimal Total { get; set; }
}

// Aggregates computed server-side for the customer dashboard, so we never have to
// transfer or re-aggregate a customer's full order history in memory.
public class CustomerOrderStats
{
    public int TotalOrders { get; set; }
    public decimal TotalRevenue { get; set; }
    public List<OrderSummary> RecentOrders { get; set; } = new();
}

public class CustomerDashboard
{
    public int CustomerId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public int TotalOrders { get; set; }
    public decimal TotalRevenue { get; set; }
    public decimal AverageOrderValue { get; set; }
    public List<OrderSummary> RecentOrders { get; set; } = new();
}

// Added for the mobile app — keep in sync with CustomerDashboard
// TODO: consolidate these two DTOs (NOVA-55)
public class CustomerSummaryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Tier { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int OrderCount { get; set; }
    public decimal LifetimeValue { get; set; }
}
