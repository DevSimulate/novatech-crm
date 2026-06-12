using Microsoft.AspNetCore.Mvc;
using NovaTechCRM.Domain.Models;
using NovaTechCRM.Services;

namespace NovaTechCRM.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    private readonly OrderService _orderService;

    public OrdersController(OrderService orderService)
    {
        _orderService = orderService;
    }

    [HttpPost]
    public async Task<IActionResult> PlaceOrder([FromBody] PlaceOrderRequest request, CancellationToken ct)
    {
        var order = new Order
        {
            CustomerId = request.CustomerId,
            TotalAmount = request.Items.Sum(i => i.Quantity * i.UnitPrice),
            Items = request.Items.Select(i => new OrderItem
            {
                ProductSku = i.ProductSku,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice
            }).ToList()
        };

        var placed = await _orderService.PlaceOrderAsync(order, ct);

        if (placed.Status == OrderStatus.Rejected)
            return UnprocessableEntity(new { error = "ORDER_REJECTED", message = "Order did not pass the fraud check and will not be fulfilled.", orderId = placed.Id });

        return CreatedAtAction(nameof(GetOrder), new { id = placed.Id }, placed);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetOrder(Guid id, CancellationToken ct)
    {
        var order = await _orderService.GetOrderAsync(id, ct);
        return order is null ? NotFound() : Ok(order);
    }

    [HttpGet("customer/{customerId}")]
    public async Task<IActionResult> GetCustomerOrders(string customerId, CancellationToken ct)
    {
        var orders = await _orderService.GetCustomerOrdersAsync(customerId, ct);
        return Ok(orders);
    }
}

public record PlaceOrderRequest(
    string CustomerId,
    List<OrderItemRequest> Items
);

public record OrderItemRequest(
    string ProductSku,
    string ProductName,
    int Quantity,
    decimal UnitPrice
);
