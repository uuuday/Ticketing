using Ticketing.Api.Contracts;

namespace Ticketing.Api.Services;

/// <summary>Handles ticket purchases with atomic inventory claiming and idempotency.</summary>
public interface IPurchaseService
{
    /// <summary>Attempts to create an order, claiming inventory atomically.</summary>
    Task<PurchaseResult> PurchaseAsync(PurchaseRequest request, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Retrieves an order by its id.</summary>
    Task<PurchaseResult> GetOrderAsync(Guid id, CancellationToken cancellationToken);
}
