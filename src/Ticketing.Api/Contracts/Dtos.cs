namespace Ticketing.Api.Contracts;

/// <summary>Request to create a pricing tier as part of event creation.</summary>
public record CreatePricingTierRequest(string Name, decimal Price, int AllocatedQuantity);

/// <summary>Request to create a new event.</summary>
public record CreateEventRequest(
    string Name,
    string? Description,
    string Venue,
    DateTimeOffset EventDateTime,
    int TotalCapacity,
    List<CreatePricingTierRequest> PricingTiers);

/// <summary>Request to update event metadata (not tiers).</summary>
public record UpdateEventRequest(
    string Name,
    string? Description,
    string Venue,
    DateTimeOffset EventDateTime,
    int TotalCapacity);

/// <summary>Pricing tier representation returned to clients.</summary>
public record PricingTierDto(Guid Id, string Name, decimal Price, int AllocatedQuantity, int SoldQuantity);

/// <summary>Event representation returned to clients.</summary>
public record EventDto(
    Guid Id,
    string Name,
    string? Description,
    string Venue,
    DateTimeOffset EventDateTime,
    int TotalCapacity,
    bool IsCancelled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    List<PricingTierDto> PricingTiers);

/// <summary>Paged result envelope.</summary>
public record PagedResult<T>(List<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>Availability of a single tier.</summary>
public record TierAvailabilityDto(Guid PricingTierId, string Name, int Allocated, int Sold, int Remaining);

/// <summary>Availability response for an event.</summary>
public record EventAvailabilityDto(Guid EventId, List<TierAvailabilityDto> Tiers);

/// <summary>A single line within a purchase request.</summary>
public record PurchaseLineRequest(Guid PricingTierId, int Quantity);

/// <summary>Request to purchase tickets for an event.</summary>
public record PurchaseRequest(Guid EventId, string CustomerRef, List<PurchaseLineRequest> Lines);

/// <summary>An order line returned to clients.</summary>
public record OrderLineDto(Guid Id, Guid PricingTierId, int Quantity, decimal UnitPrice);

/// <summary>An order returned to clients.</summary>
public record OrderDto(
    Guid Id,
    Guid EventId,
    string IdempotencyKey,
    string CustomerRef,
    decimal TotalAmount,
    DateTimeOffset CreatedAt,
    List<OrderLineDto> Lines);

/// <summary>Sales figures for a single tier.</summary>
public record TierSalesDto(Guid PricingTierId, string Name, int UnitsSold, decimal Revenue);

/// <summary>Sales report for an event.</summary>
public record SalesReportDto(Guid EventId, List<TierSalesDto> Tiers, int TotalUnitsSold, decimal TotalRevenue);
