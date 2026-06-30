using Microsoft.Extensions.Logging;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Repositories;
using NovaTechCRM.Services.Interfaces;

namespace NovaTechCRM.Services;

public class ReportService : IReportService
{
    private readonly IReportRepository _reportRepo;
    private readonly IOrderRepository _orderRepo;
    private readonly ICacheService _cache;
    private readonly ILogger<ReportService> _logger;

    private static readonly Dictionary<Guid, CancellationTokenSource> _activeReports = new();

    public ReportService(
        IReportRepository reportRepo,
        IOrderRepository orderRepo,
        ICacheService cache,
        ILogger<ReportService> logger)
    {
        _reportRepo = reportRepo;
        _orderRepo  = orderRepo;
        _cache      = cache;
        _logger     = logger;
    }

    public async Task<Report> RequestAsync(
        ReportType type, DateTime periodStart, DateTime periodEnd,
        string userId, ReportFormat format = ReportFormat.Json,
        CancellationToken ct = default)
    {
        var report = new Report
        {
            Type         = type,
            Format       = format,
            PeriodStart  = periodStart,
            PeriodEnd    = periodEnd,
            RequestedAt  = DateTime.UtcNow,
            Status       = ReportStatus.Queued,
            RequestedByUserId = userId,
            ExpiresAt    = DateTime.UtcNow.AddDays(7)
        };

        report.Name = $"{type} {periodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd}";

        var created = await _reportRepo.CreateAsync(report, ct);

        var cts = new CancellationTokenSource();
        _activeReports[created.Id] = cts;  // BUG: never cleaned up if exception thrown before ProcessPending

        _logger.LogInformation("Report {ReportId} queued: {Type}", created.Id, type);

        return created;
    }

    public async Task<Report?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _reportRepo.GetByIdAsync(id, ct);

    public async Task<IReadOnlyList<Report>> GetByUserAsync(
        string userId, CancellationToken ct = default) =>
        await _reportRepo.GetByUserAsync(userId, ct);

    public async Task ProcessPendingAsync(CancellationToken ct = default)
    {
        var pending = await _reportRepo.GetByStatusAsync(ReportStatus.Queued, ct);

        foreach (var report in pending)
        {
            if (!_activeReports.TryGetValue(report.Id, out var cts))
                continue;

            report.Status    = ReportStatus.Running;
            report.StartedAt = DateTime.UtcNow;
            await _reportRepo.UpdateAsync(report, ct);

            try
            {
                var data = await GenerateReportDataAsync(report, cts.Token);
                report.ResultJson     = data;
                report.Status         = ReportStatus.Completed;
                report.CompletedAt    = DateTime.UtcNow;
                await _reportRepo.UpdateAsync(report, ct);

                _logger.LogInformation("Report {ReportId} completed", report.Id);
                _activeReports.Remove(report.Id);
                cts.Dispose();
            }
            catch (Exception ex)
            {
                report.Status       = ReportStatus.Failed;
                report.ErrorMessage = ex.Message;
                await _reportRepo.UpdateAsync(report, ct);

                _activeReports.Remove(report.Id);
                cts.Dispose();
                _logger.LogError(ex, "Report {ReportId} failed", report.Id);
            }
        }
    }

    private async Task<string> GenerateReportDataAsync(Report report, CancellationToken ct)
    {
        return report.Type switch
        {
            ReportType.SalesSummary => await GenerateSalesSummaryAsync(report, ct),
            _                       => "{}"
        };
    }

    private async Task<string> GenerateSalesSummaryAsync(Report report, CancellationToken ct)
    {
        var cacheKey = $"sales_{report.PeriodStart:yyyyMMdd}_{report.PeriodEnd:yyyyMMdd}";
        var cached   = await _cache.GetAsync<string>(cacheKey, ct);
        if (cached != null) return cached;

        var orders = await _orderRepo.GetByDateRangeAsync(report.PeriodStart, report.PeriodEnd, ct);
        var json   = System.Text.Json.JsonSerializer.Serialize(orders.Select(o => new
        {
            o.Id, o.CustomerId, o.TotalAmount, o.Status, o.CreatedAt
        }));

        await _cache.SetAsync(cacheKey, json, TimeSpan.FromMinutes(30), ct);
        return json;
    }

    public async Task<IReadOnlyList<SalesReportRow>> GetSalesSummaryAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var cacheKey = $"sales_summary_{from:yyyyMMdd}_{to:yyyyMMdd}";
        var cached   = await _cache.GetAsync<List<SalesReportRow>>(cacheKey, ct);
        if (cached != null) return cached;

        var orders = await _orderRepo.GetByDateRangeAsync(from, to, ct);

        var rows = orders
            .GroupBy(o => o.CreatedAt.Date)
            .OrderBy(g => g.Key)
            .Select(g => new SalesReportRow
            {
                Date             = g.Key,
                OrderCount       = g.Count(),
                GrossRevenue     = g.Sum(o => o.TotalAmount),
                NetRevenue       = g.Where(o => o.Status == OrderStatus.Fulfilled)
                                    .Sum(o => o.TotalAmount),
                AverageOrderValue = g.Average(o => o.TotalAmount)
            })
            .ToList();

        await _cache.SetAsync(cacheKey, rows, TimeSpan.FromMinutes(30), ct);
        return rows;
    }

    public async Task<IReadOnlyList<RevenueByProductRow>> GetRevenueByProductAsync(
        DateTime from, DateTime to, CancellationToken ct = default)
    {
        var orders = await _orderRepo.GetByDateRangeAsync(from, to, ct);

        return orders
            .Where(o => o.Status == OrderStatus.Fulfilled)
            .SelectMany(o => o.Items)
            .GroupBy(i => i.ProductSku)
            .Select(g => new RevenueByProductRow
            {
                ProductSku  = g.Key,
                ProductName = g.First().ProductName,
                UnitsSold   = g.Sum(i => i.Quantity),
                Revenue     = g.Sum(i => i.Quantity * i.UnitPrice)
            })
            .OrderByDescending(r => r.Revenue)
            .ToList();
    }

    public async Task<IReadOnlyList<CustomerRetentionRow>> GetCustomerRetentionAsync(
        int year, CancellationToken ct = default)
    {
        // TODO: implement properly — placeholder returns empty (NOVA-66)
        await Task.CompletedTask;
        return Array.Empty<CustomerRetentionRow>();
    }

    public async Task<ReportSchedule> CreateScheduleAsync(
        ReportSchedule schedule, CancellationToken ct = default)
    {
        schedule.CreatedAt = DateTime.UtcNow;
        return await _reportRepo.CreateScheduleAsync(schedule, ct);
    }

    public async Task<IReadOnlyList<ReportSchedule>> GetSchedulesAsync(
        CancellationToken ct = default) =>
        await _reportRepo.GetSchedulesAsync(ct);

    public async Task RunScheduledReportsAsync(CancellationToken ct = default)
    {
        var schedules = await _reportRepo.GetDueSchedulesAsync(DateTime.UtcNow, ct);

        foreach (var schedule in schedules.Where(s => s.IsActive))
        {
            try
            {
                // calculate period based on schedule type
                var (start, end) = schedule.CronExpression switch
                {
                    var c when c.Contains("* * 1") => // monthly
                        (DateTime.UtcNow.AddMonths(-1), DateTime.UtcNow),
                    _ => (DateTime.UtcNow.AddDays(-7), DateTime.UtcNow)
                };

                await RequestAsync(schedule.ReportType, start, end,
                    schedule.CreatedByUserId, schedule.Format, ct);

                schedule.LastRunAt  = DateTime.UtcNow;
                schedule.RunCount++;
                await _reportRepo.UpdateScheduleAsync(schedule, ct);
            }
            catch (Exception ex)
            {
                schedule.FailureCount++;
                await _reportRepo.UpdateScheduleAsync(schedule, ct);
                _logger.LogError(ex, "Scheduled report {ScheduleId} failed", schedule.Id);
            }
        }
    }
}
