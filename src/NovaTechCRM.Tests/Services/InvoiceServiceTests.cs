using Moq;
using Xunit;
using Microsoft.Extensions.Logging;
using NovaTechCRM.Domain.Exceptions;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Repositories;
using NovaTechCRM.Services;
using NovaTechCRM.Services.Interfaces;
using NovaTechCRM.Tests.Builders;

namespace NovaTechCRM.Tests.Services;

public class InvoiceServiceTests
{
    private readonly Mock<IInvoiceRepository>      _invoiceRepo  = new();
    private readonly Mock<ICustomerRepository>     _customerRepo = new();
    private readonly Mock<INotificationService>    _notify       = new();
    private readonly Mock<IPdfGeneratorService>    _pdf          = new();
    private readonly Mock<IAuditService>           _audit        = new();
    private readonly Mock<ILogger<InvoiceService>> _logger       = new();

    private InvoiceService CreateSut() => new(
        _invoiceRepo.Object, _customerRepo.Object, _notify.Object,
        _pdf.Object, _audit.Object, _logger.Object);

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        _invoiceRepo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), default))
            .ReturnsAsync((Invoice?)null);

        var result = await CreateSut().GetByIdAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task IssueAsync_TransitionsDraftToIssued()
    {
        var invoice = new InvoiceBuilder()
            .WithStatus(InvoiceStatus.Draft)
            .Build();

        _invoiceRepo.Setup(r => r.GetByIdAsync(invoice.Id, default)).ReturnsAsync(invoice);
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<Invoice>(), default))
            .ReturnsAsync((Invoice i, CancellationToken _) => i);

        var result = await CreateSut().IssueAsync(invoice.Id);

        Assert.Equal(InvoiceStatus.Issued, result.Status);
        Assert.NotNull(result.IssuedAt);
    }

    [Fact]
    public async Task IssueAsync_Throws_WhenNotDraft()
    {
        var invoice = new InvoiceBuilder().WithStatus(InvoiceStatus.Issued).Build();
        _invoiceRepo.Setup(r => r.GetByIdAsync(invoice.Id, default)).ReturnsAsync(invoice);

        await Assert.ThrowsAsync<InvoiceException>(
            () => CreateSut().IssueAsync(invoice.Id));
    }

    [Fact]
    public async Task RecordPaymentAsync_MarksFullyPaidWhenAmountCoversTotal()
    {
        var invoice = new InvoiceBuilder()
            .WithStatus(InvoiceStatus.Issued)
            .WithTotal(500m)
            .WithAmountPaid(0m)
            .Build();

        _invoiceRepo.Setup(r => r.GetByIdAsync(invoice.Id, default)).ReturnsAsync(invoice);
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<Invoice>(), default))
            .ReturnsAsync((Invoice i, CancellationToken _) => i);
        _invoiceRepo.Setup(r => r.AddPaymentRecordAsync(It.IsAny<InvoicePaymentRecord>(), default))
            .Returns(Task.CompletedTask);

        var result = await CreateSut().RecordPaymentAsync(invoice.Id, 500m, Guid.NewGuid());

        Assert.Equal(InvoiceStatus.Paid, result.Status);
        Assert.NotNull(result.PaidAt);
    }

    [Fact]
    public async Task RecordPaymentAsync_MarksPartiallyPaid_WhenLessThanTotal()
    {
        var invoice = new InvoiceBuilder()
            .WithStatus(InvoiceStatus.Issued)
            .WithTotal(500m)
            .WithAmountPaid(0m)
            .Build();

        _invoiceRepo.Setup(r => r.GetByIdAsync(invoice.Id, default)).ReturnsAsync(invoice);
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<Invoice>(), default))
            .ReturnsAsync((Invoice i, CancellationToken _) => i);
        _invoiceRepo.Setup(r => r.AddPaymentRecordAsync(It.IsAny<InvoicePaymentRecord>(), default))
            .Returns(Task.CompletedTask);

        var result = await CreateSut().RecordPaymentAsync(invoice.Id, 200m, Guid.NewGuid());

        Assert.Equal(InvoiceStatus.PartiallyPaid, result.Status);
        Assert.Equal(200m, result.AmountPaid);
    }

    [Fact]
    public async Task RecordPaymentAsync_Throws_WhenVoided()
    {
        var invoice = new InvoiceBuilder().WithStatus(InvoiceStatus.Voided).Build();
        _invoiceRepo.Setup(r => r.GetByIdAsync(invoice.Id, default)).ReturnsAsync(invoice);

        await Assert.ThrowsAsync<InvoiceException>(
            () => CreateSut().RecordPaymentAsync(invoice.Id, 100m, Guid.NewGuid()));
    }

    [Fact]
    public async Task VoidAsync_SetsVoidedStatusAndAudits()
    {
        var invoice = new InvoiceBuilder().WithStatus(InvoiceStatus.Issued).Build();
        _invoiceRepo.Setup(r => r.GetByIdAsync(invoice.Id, default)).ReturnsAsync(invoice);
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<Invoice>(), default))
            .ReturnsAsync((Invoice i, CancellationToken _) => i);

        await CreateSut().VoidAsync(invoice.Id, "Duplicate invoice");

        _audit.Verify(a => a.LogAsync(
            AuditAction.StatusChanged, "Invoice", invoice.Id.ToString(),
            It.IsAny<string?>(), It.IsAny<object?>(), It.IsAny<object?>(),
            default), Times.Once);
    }

    [Fact]
    public async Task SendAsync_GeneratesPdf_WhenMissing()
    {
        var invoice = new InvoiceBuilder()
            .WithStatus(InvoiceStatus.Issued)
            .Build();

        invoice.PdfUrl = null;

        _invoiceRepo.Setup(r => r.GetByIdAsync(invoice.Id, default)).ReturnsAsync(invoice);
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<Invoice>(), default))
            .ReturnsAsync((Invoice i, CancellationToken _) => i);
        _pdf.Setup(p => p.GenerateInvoicePdfAsync(It.IsAny<Invoice>(), default))
            .ReturnsAsync("https://storage.example.com/invoices/INV-2024-00001.pdf");

        await CreateSut().SendAsync(invoice.Id);

        _pdf.Verify(p => p.GenerateInvoicePdfAsync(It.IsAny<Invoice>(), default), Times.Once);
        _notify.Verify(n => n.SendInvoiceAsync(It.IsAny<Invoice>(), default), Times.Once);
    }

    [Fact]
    public async Task ProcessOverdueAsync_DoesNotNotify_WhenWithinThreeBusinessDayGracePeriod()
    {
        // 1 calendar day past due — within the 3 business day grace period
        var invoice = new InvoiceBuilder()
            .WithStatus(InvoiceStatus.Issued)
            .WithDueAt(DateTime.UtcNow.AddDays(-1))
            .Build();

        _invoiceRepo.Setup(r => r.GetByStatusAsync(InvoiceStatus.Issued, default))
            .ReturnsAsync(new List<Invoice> { invoice });

        await CreateSut().ProcessOverdueAsync();

        _notify.Verify(n => n.SendInvoiceOverdueAsync(It.IsAny<Invoice>(), default), Times.Never);
        _invoiceRepo.Verify(r => r.UpdateAsync(It.IsAny<Invoice>(), default), Times.Never);
        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
    }

    [Fact]
    public async Task ProcessOverdueAsync_MarksOverdueAndNotifies_AfterGracePeriodExpires()
    {
        // 10 calendar days past due — guaranteed to exceed the 3 business day grace period
        var invoice = new InvoiceBuilder()
            .WithStatus(InvoiceStatus.Issued)
            .Overdue()
            .Build();

        _invoiceRepo.Setup(r => r.GetByStatusAsync(InvoiceStatus.Issued, default))
            .ReturnsAsync(new List<Invoice> { invoice });
        _invoiceRepo.Setup(r => r.UpdateAsync(It.IsAny<Invoice>(), default))
            .ReturnsAsync((Invoice i, CancellationToken _) => i);

        await CreateSut().ProcessOverdueAsync();

        Assert.Equal(InvoiceStatus.Overdue, invoice.Status);
        _notify.Verify(n => n.SendInvoiceOverdueAsync(invoice, default), Times.Once);
    }

    [Fact]
    public async Task CreateFromOrderAsync_CalculatesTotalsFromLineItems()
    {
        var order = Orders.Simple();
        order.Items = new List<OrderItem>
        {
            new() { ProductSku = "A", ProductName = "Item A", Quantity = 2, UnitPrice = 100m },
            new() { ProductSku = "B", ProductName = "Item B", Quantity = 1, UnitPrice = 50m  },
        };

        var customer = new CustomerBuilder().Build();
        _customerRepo.Setup(r => r.GetByIdAsync(It.IsAny<int>(), default))
            .ReturnsAsync(customer);
        _invoiceRepo.Setup(r => r.GetNextSequenceAsync(It.IsAny<int>(), default))
            .ReturnsAsync(42);
        _invoiceRepo.Setup(r => r.CreateAsync(It.IsAny<Invoice>(), default))
            .ReturnsAsync((Invoice i, CancellationToken _) => i);

        var result = await CreateSut().CreateFromOrderAsync(order);

        Assert.Equal(2, result.LineItems.Count);
        Assert.Equal(250m, result.SubTotal);
    }
}
