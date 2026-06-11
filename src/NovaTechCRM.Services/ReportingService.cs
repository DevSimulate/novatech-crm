using System.Collections.Concurrent;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Repositories;

namespace NovaTechCRM.Services;

public interface IReportingService
{
    Task<CustomerDashboard?> GetCustomerDashboardAsync(int customerId, CancellationToken ct);
}

public class ReportingService : IReportingService
{
    private readonly ICustomerRepository _customerRepo;

    private static readonly ConcurrentDictionary<int, CustomerDashboard> _reportingCache = new();

    public ReportingService(ICustomerRepository customerRepo)
    {
        _customerRepo = customerRepo;
    }

    public async Task<CustomerDashboard?> GetCustomerDashboardAsync(int customerId, CancellationToken ct)
    {
        if (_reportingCache.TryGetValue(customerId, out var cached))
            return cached;

        var customer = await _customerRepo.GetByIdAsync(customerId, ct);
        if (customer is null) return null;

        var stats = await _customerRepo.GetCustomerOrderStatsAsync(customerId, ct);

        var dashboard = new CustomerDashboard
        {
            CustomerId = customer.Id,
            CustomerName = customer.Name,
            TotalOrders = stats.TotalOrders,
            TotalRevenue = stats.TotalRevenue,
            AverageOrderValue = stats.TotalOrders > 0
                ? stats.TotalRevenue / stats.TotalOrders
                : 0,
            RecentOrders = stats.RecentOrders,
        };

        _reportingCache[customerId] = dashboard;

        return dashboard;
    }
}
