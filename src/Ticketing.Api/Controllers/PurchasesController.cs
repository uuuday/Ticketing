using Microsoft.AspNetCore.Mvc;
using Ticketing.Api.Contracts;
using Ticketing.Api.Domain;
using Ticketing.Api.Services;

namespace Ticketing.Api.Controllers;

/// <summary>Handles ticket purchase requests.</summary>
[ApiController]
[Route("api/purchases")]
public class PurchasesController : ControllerBase
{
    private const string IdempotencyHeaderName = "Idempotency-Key";

    private readonly IPurchaseService _purchaseService;

    public PurchasesController(IPurchaseService purchaseService)
    {
        _purchaseService = purchaseService;
    }

    /// <summary>Purchases tickets for an event. Requires an Idempotency-Key header.</summary>
    /// <remarks>
    /// Retrying with the same Idempotency-Key returns the original order (200) instead of
    /// creating a duplicate, so a client retry after a timeout cannot double-charge.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(OrderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Purchase(
        [FromHeader(Name = IdempotencyHeaderName)] string? idempotencyKey,
        [FromBody] PurchaseRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Missing Idempotency-Key",
                Detail = "An Idempotency-Key header is required so that a retried request "
                       + "cannot result in a duplicate purchase.",
                Status = StatusCodes.Status400BadRequest,
                Instance = HttpContext.Request.Path
            });
        }

        var result = await _purchaseService.PurchaseAsync(request, idempotencyKey.Trim(), cancellationToken);

        return result.Status switch
        {
            PurchaseStatus.Created =>
                CreatedAtAction(nameof(GetOrder), new { id = result.Order!.Id }, ToDto(result.Order)),

            PurchaseStatus.Replayed =>
                Ok(ToDto(result.Order!)),

            PurchaseStatus.NotFound =>
                NotFound(Problem7807("Resource not found", result.Error, StatusCodes.Status404NotFound)),

            PurchaseStatus.Conflict =>
                Conflict(Problem7807("Conflict", result.Error, StatusCodes.Status409Conflict)),

            PurchaseStatus.Invalid =>
                UnprocessableEntity(Problem7807("Validation failed", result.Error, StatusCodes.Status422UnprocessableEntity)),

            // Throwing rather than returning 500 means a newly added PurchaseStatus
            // fails loudly in testing instead of silently in production.
            _ => throw new InvalidOperationException($"Unhandled purchase status: {result.Status}")
        };
    }

    private ProblemDetails Problem7807(string title, string? detail, int status) => new()
    {
        Title = title,
        Detail = detail,
        Status = status,
        Instance = HttpContext.Request.Path
    };

    /// <summary>Gets an order by id.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetOrder(Guid id, CancellationToken cancellationToken)
    {
        var result = await _purchaseService.GetOrderAsync(id, cancellationToken);
        return result.Status == PurchaseStatus.NotFound
            ? NotFound(new ProblemDetails { Title = result.Error, Status = StatusCodes.Status404NotFound })
            : Ok(ToDto(result.Order!));
    }

    private static OrderDto ToDto(Order order) => new(
        order.Id,
        order.EventId,
        order.IdempotencyKey,
        order.CustomerRef,
        order.TotalAmount,
        order.CreatedAt,
        order.Lines.Select(l => new OrderLineDto(l.Id, l.PricingTierId, l.Quantity, l.UnitPrice)).ToList());
}
