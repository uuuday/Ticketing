using Ticketing.Api.Domain;

namespace Ticketing.Api.Services;

/// <summary>Outcome status of a purchase attempt.</summary>
public enum PurchaseStatus
{
    Created,
    Replayed,
    Found,
    NotFound,
    Conflict,
    Invalid
}

/// <summary>Result of a purchase attempt.</summary>
public record PurchaseResult(PurchaseStatus Status, Order? Order, string? Error);
