using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NovaTechCRM.Domain.Models;

namespace NovaTechCRM.Repositories;

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(int customerId, CancellationToken ct = default);
    Task<(IReadOnlyList<Customer> Items, int Total)> GetAllAsync(
        int page, int pageSize, string? search = null, CustomerStatus? status = null,
        CustomerTier? tier = null, CancellationToken ct = default);
    Task<IReadOnlyList<Customer>> SearchAsync(string query, CancellationToken ct = default);
    Task<Customer> CreateAsync(Customer customer, CancellationToken ct = default);
    Task<Customer> UpdateAsync(Customer customer, CancellationToken ct = default);
    Task<IReadOnlyList<Customer>> GetActiveSinceAsync(DateTime since, CancellationToken ct = default);
    Task<IReadOnlyList<Customer>> GetByTierAsync(CustomerTier tier, CancellationToken ct = default);
    Task BulkUpdateTierAsync(IEnumerable<int> customerIds, CustomerTier tier, CancellationToken ct = default);

    // Dashboard stats — aggregates + recent orders computed in SQL (no full-history transfer).
    Task<CustomerOrderStats> GetCustomerOrderStatsAsync(int customerId, CancellationToken ct = default);

    // Legacy — kept for backward compat with old reporting queries
    Task<List<OrderSummary>> GetOrderSummariesAsync(int customerId, CancellationToken ct = default);
}

public class CustomerRepository : ICustomerRepository
{
    private readonly NovaTechDbContext _db;
    private readonly string _connectionString;

    public CustomerRepository(NovaTechDbContext db, string connectionString)
    {
        _db               = db;
        _connectionString = connectionString;
    }

    public async Task<Customer?> GetByIdAsync(int customerId, CancellationToken ct = default)
        => await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, ct);

    public async Task<(IReadOnlyList<Customer> Items, int Total)> GetAllAsync(
        int page, int pageSize, string? search = null, CustomerStatus? status = null,
        CustomerTier? tier = null, CancellationToken ct = default)
    {
        var q = _db.Customers.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(c => c.Name.Contains(search) || c.Email.Contains(search));

        if (status.HasValue)
            q = q.Where(c => c.Status == status.Value);

        if (tier.HasValue)
            q = q.Where(c => c.Tier == tier.Value);

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<IReadOnlyList<Customer>> SearchAsync(string query, CancellationToken ct = default)
    {
        // LIKE search — acceptable for now, see NOVA-56 for full-text index plan
        return await _db.Customers
            .AsNoTracking()
            .Where(c => c.Name.Contains(query) || c.Email.Contains(query))
            .Take(50)
            .ToListAsync(ct);
    }

    public async Task<Customer> CreateAsync(Customer customer, CancellationToken ct = default)
    {
        customer.CreatedAt = DateTime.UtcNow;
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(ct);
        return customer;
    }

    public async Task<Customer> UpdateAsync(Customer customer, CancellationToken ct = default)
    {
        customer.UpdatedAt = DateTime.UtcNow;
        _db.Customers.Update(customer);
        await _db.SaveChangesAsync(ct);
        return customer;
    }

    public async Task<IReadOnlyList<Customer>> GetActiveSinceAsync(
        DateTime since, CancellationToken ct = default)
        => await _db.Customers
            .AsNoTracking()
            .Where(c => c.Status == CustomerStatus.Active && c.CreatedAt >= since)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Customer>> GetByTierAsync(
        CustomerTier tier, CancellationToken ct = default)
        => await _db.Customers
            .AsNoTracking()
            .Where(c => c.Tier == tier)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

    public async Task BulkUpdateTierAsync(
        IEnumerable<int> customerIds, CustomerTier tier, CancellationToken ct = default)
    {
        var ids = customerIds.ToList();
        await _db.Customers
            .Where(c => ids.Contains(c.Id))
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.Tier,      tier)
                       .SetProperty(c => c.UpdatedAt, DateTime.UtcNow),
                ct);
    }

    // --- Legacy ADO.NET below — these predate EF Core being added to this project ---

    // Powers the customer dashboard. Aggregates (count/revenue) and the 10 most recent
    // orders are both computed in SQL, in a single round trip, so cost no longer scales
    // with the customer's total order history.
    //
    // NOTE (NOVA-52): this query relies on an index on Orders(CustomerId) and
    // LineItems(OrderId) to stay fast on large/legacy customers. Prod is on migration
    // v18 and migrations are applied manually by infra (see NovaTechDbContext), so the
    // index is NOT created here. Have infra apply:
    //     CREATE INDEX IX_Orders_CustomerId ON Orders (CustomerId) INCLUDE (Status, CreatedAt);
    //     CREATE INDEX IX_LineItems_OrderId ON LineItems (OrderId) INCLUDE (Quantity, UnitPrice);
    public async Task<CustomerOrderStats> GetCustomerOrderStatsAsync(
        int customerId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(@"
            SELECT COUNT(*)                  AS TotalOrders,
                   COALESCE(SUM(t.Total), 0) AS TotalRevenue
            FROM (
                SELECT o.Id, SUM(li.Quantity * li.UnitPrice) AS Total
                FROM Orders o
                JOIN LineItems li ON li.OrderId = o.Id
                WHERE o.CustomerId = @cid
                GROUP BY o.Id
            ) t;

            SELECT TOP (10) o.Id, o.Status, o.CreatedAt,
                   SUM(li.Quantity * li.UnitPrice) AS Total
            FROM Orders o
            JOIN LineItems li ON li.OrderId = o.Id
            WHERE o.CustomerId = @cid
            GROUP BY o.Id, o.Status, o.CreatedAt
            ORDER BY o.CreatedAt DESC;", conn);

        // CustomerId is a string column (see Order.CustomerId). Bind a string parameter so
        // the predicate stays sargable — binding an int forces a per-row CONVERT on the
        // column, which makes any index on Orders(CustomerId) unusable (full scan).
        cmd.Parameters.Add("@cid", SqlDbType.NVarChar, 100).Value = customerId.ToString();

        var stats = new CustomerOrderStats();

        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
        {
            stats.TotalOrders  = reader.GetInt32(0);
            stats.TotalRevenue = reader.GetDecimal(1);
        }

        await reader.NextResultAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            stats.RecentOrders.Add(new OrderSummary
            {
                OrderId   = reader.GetInt32(0),
                Status    = reader.GetString(1),
                CreatedAt = reader.GetDateTime(2),
                Total     = reader.GetDecimal(3),
            });
        }

        return stats;
    }

    public async Task<List<OrderSummary>> GetOrderSummariesAsync(
        int customerId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new SqlCommand(@"
            SELECT o.Id, o.Status, o.CreatedAt, SUM(li.Quantity * li.UnitPrice) AS Total
            FROM Orders o
            JOIN LineItems li ON li.OrderId = o.Id
            WHERE o.CustomerId = @cid
            GROUP BY o.Id, o.Status, o.CreatedAt
            ORDER BY o.CreatedAt DESC", conn);

        cmd.Parameters.AddWithValue("@cid", customerId);

        var summaries = new List<OrderSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            summaries.Add(new OrderSummary
            {
                OrderId   = reader.GetInt32(0),
                Status    = reader.GetString(1),
                CreatedAt = reader.GetDateTime(2),
                Total     = reader.GetDecimal(3),
            });
        }

        return summaries;
    }
}
