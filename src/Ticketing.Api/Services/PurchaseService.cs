using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Contracts;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Telemetry;

namespace Ticketing.Api.Services;

/// <summary>
/// Implements ticket purchases using a single atomic conditional UPDATE per pricing tier
/// to claim inventory, avoiding lost-update races. Also implements idempotency via a
/// unique index on Order.IdempotencyKey combined with catching unique-violation errors
/// from concurrent duplicate requests.
/// </summary>
public class PurchaseService : IPurchaseService
{
    private readonly TicketingDbContext _db;
    private readonly ILogger<PurchaseService> _logger;

    public PurchaseService(TicketingDbContext db, ILogger<PurchaseService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PurchaseResult> PurchaseAsync(PurchaseRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        using var activity = TicketingTelemetry.ActivitySource.StartActivity("purchase.execute");
        activity?.SetTag("event.id", request.EventId);
        activity?.SetTag("order.line_count", request.Lines?.Count ?? 0);
        activity?.SetTag("ticket.total_quantity", request.Lines?.Sum(l => l.Quantity) ?? 0);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await PurchaseCoreAsync(request, idempotencyKey, cancellationToken);

            var outcome = ToOutcomeTag(result.Status);
            activity?.SetTag("purchase.outcome", outcome);
            if (result.Status is PurchaseStatus.Conflict or PurchaseStatus.Invalid or PurchaseStatus.NotFound)
            {
                activity?.SetStatus(ActivityStatusCode.Error, result.Error);
            }

            TicketingTelemetry.PurchaseDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", outcome));

            return result;
        }
        catch (Exception ex)
        {
            activity?.SetTag("purchase.outcome", "error");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            TicketingTelemetry.PurchaseDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("outcome", "error"));
            throw;
        }
    }

    private async Task<PurchaseResult> PurchaseCoreAsync(PurchaseRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return new PurchaseResult(PurchaseStatus.Invalid, null, "Idempotency-Key is required.");
        }

        var existing = await _db.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            TicketingTelemetry.IdempotentReplays.Add(1);
            _logger.LogInformation("Order {OrderId} replayed for event {EventId} via idempotency key", existing.Id, existing.EventId);
            return new PurchaseResult(PurchaseStatus.Replayed, existing, null);
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            _logger.LogWarning("Purchase request for event {EventId} failed validation: no purchase lines provided", request.EventId);
            return new PurchaseResult(PurchaseStatus.Invalid, null, "At least one purchase line is required.");
        }

        foreach (var line in request.Lines)
        {
            if (line.Quantity < 1 || line.Quantity > 50)
            {
                _logger.LogWarning("Purchase request for event {EventId} failed validation: quantity {Quantity} out of range for tier {PricingTierId}", request.EventId, line.Quantity, line.PricingTierId);
                return new PurchaseResult(PurchaseStatus.Invalid, null, "Quantity must be between 1 and 50.");
            }
        }

        var duplicateTierIds = request.Lines
            .GroupBy(l => l.PricingTierId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateTierIds.Count > 0)
        {
            _logger.LogWarning("Purchase request for event {EventId} failed validation: duplicate pricing tier in request", request.EventId);
            return new PurchaseResult(PurchaseStatus.Invalid, null, "Duplicate pricing tier in purchase request.");
        }

        var ticketEvent = await _db.Events
            .Include(e => e.PricingTiers)
            .FirstOrDefaultAsync(e => e.Id == request.EventId, cancellationToken);
        if (ticketEvent is null)
        {
            _logger.LogWarning("Purchase request failed: event {EventId} not found", request.EventId);
            return new PurchaseResult(PurchaseStatus.NotFound, null, "Event not found.");
        }

        if (ticketEvent.IsCancelled || ticketEvent.EventDateTime <= DateTimeOffset.UtcNow)
        {
            _logger.LogWarning("Purchase request rejected for event {EventId}: event is cancelled or already started", request.EventId);
            return new PurchaseResult(PurchaseStatus.Conflict, null, "Event is cancelled or has already started.");
        }

        var tiersById = ticketEvent.PricingTiers.ToDictionary(t => t.Id);
        foreach (var line in request.Lines)
        {
            if (!tiersById.ContainsKey(line.PricingTierId))
            {
                _logger.LogWarning("Purchase request failed: pricing tier {PricingTierId} not found for event {EventId}", line.PricingTierId, request.EventId);
                return new PurchaseResult(PurchaseStatus.NotFound, null, "Pricing tier not found.");
            }
        }

        var orderedLines = request.Lines.OrderBy(l => l.PricingTierId).ToList();

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var line in orderedLines)
            {
                using var claimActivity = TicketingTelemetry.ActivitySource.StartActivity("inventory.claim");
                claimActivity?.SetTag("tier.id", line.PricingTierId);
                claimActivity?.SetTag("tier.requested_quantity", line.Quantity);

                var affected = await _db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE PricingTiers SET SoldQuantity = SoldQuantity + {line.Quantity} WHERE Id = {line.PricingTierId} AND SoldQuantity + {line.Quantity} <= AllocatedQuantity",
                    cancellationToken);

                if (affected == 0)
                {
                    claimActivity?.SetTag("claim.succeeded", false);
                    claimActivity?.SetStatus(ActivityStatusCode.Error, "Insufficient inventory.");

                    var tags = new TagList
                    {
                        { "event_id", request.EventId.ToString() },
                        { "tier_id", line.PricingTierId.ToString() }
                    };
                    TicketingTelemetry.OversellAttempts.Add(1, tags);

                    _logger.LogWarning("Oversell attempt rejected for tier {PricingTierId} on event {EventId}: requested {Quantity}",
                        line.PricingTierId, request.EventId, line.Quantity);

                    try { await transaction.RollbackAsync(cancellationToken); }
                    catch (Exception rollbackEx)
                    {
                        _logger.LogError(rollbackEx, "Rollback failed after unique violation on key {Key}", idempotencyKey);
                    }
                    return new PurchaseResult(PurchaseStatus.Conflict, null, "Insufficient inventory for one or more tiers.");
                }

                claimActivity?.SetTag("claim.succeeded", true);
            }

            var order = new Order
            {
                Id = Guid.NewGuid(),
                EventId = ticketEvent.Id,
                IdempotencyKey = idempotencyKey,
                CustomerRef = request.CustomerRef,
                CreatedAt = DateTimeOffset.UtcNow,
                Lines = new List<OrderLine>()
            };

            decimal total = 0;
            foreach (var line in orderedLines)
            {
                var tier = tiersById[line.PricingTierId];
                var lineTotal = tier.Price * line.Quantity;
                total += lineTotal;
                order.Lines.Add(new OrderLine
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    PricingTierId = tier.Id,
                    Quantity = line.Quantity,
                    UnitPrice = tier.Price
                });
            }

            order.TotalAmount = total;

            _db.Orders.Add(order);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            foreach (var line in orderedLines)
            {
                var tags = new TagList
                {
                    { "event_id", request.EventId.ToString() },
                    { "tier_id", line.PricingTierId.ToString() }
                };
                TicketingTelemetry.TicketsPurchased.Add(line.Quantity, tags);
                TicketingTelemetry.TicketsRemaining.Add(-line.Quantity, tags);
            }

            _logger.LogInformation("Order {OrderId} created for event {EventId} with {LineCount} lines totalling {TotalAmount}",
                order.Id, ticketEvent.Id, order.Lines.Count, order.TotalAmount);

            return new PurchaseResult(PurchaseStatus.Created, order, null);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await transaction.RollbackAsync(cancellationToken);

            var winning = await _db.Orders.AsNoTracking()
                .Include(o => o.Lines)
                .FirstOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, cancellationToken);

            if (winning is null)
            {
                _logger.LogError("Unique violation on key {Key} but no winning order found", idempotencyKey);
                return new PurchaseResult(PurchaseStatus.Conflict, null, "Concurrent request conflict; please retry.");
            }

            TicketingTelemetry.IdempotentReplays.Add(1);
            _logger.LogInformation("Order replayed for event {EventId} after concurrent duplicate request with idempotency key", request.EventId);

            return new PurchaseResult(PurchaseStatus.Replayed, winning, null);
        }
    }

    /// <inheritdoc />
    public async Task<PurchaseResult> GetOrderAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await _db.Orders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        return order is null
            ? new PurchaseResult(PurchaseStatus.NotFound, null, "Order not found.")
            : new PurchaseResult(PurchaseStatus.Found, order, null);
    }

    private static string ToOutcomeTag(PurchaseStatus status) => status switch
    {
        PurchaseStatus.Created => "created",
        PurchaseStatus.Replayed => "replayed",
        PurchaseStatus.Found => "found",
        PurchaseStatus.Conflict => "conflict",
        PurchaseStatus.Invalid => "invalid",
        PurchaseStatus.NotFound => "not_found",
        _ => "unknown"
    };

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        return ex.InnerException is SqlException sqlEx && (sqlEx.Number == 2627 || sqlEx.Number == 2601);
    }
}
