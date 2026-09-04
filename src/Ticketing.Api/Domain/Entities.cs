namespace Ticketing.Api.Domain;

/// <summary>Represents a ticketed event.</summary>
public class Event
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string? Description { get; set; }
    public string Venue { get; set; } = default!;
    public DateTimeOffset EventDateTime { get; set; }
    public int TotalCapacity { get; set; }
    public bool IsCancelled { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public List<PricingTier> PricingTiers { get; set; } = new();
}

/// <summary>Represents a pricing tier of tickets for an event.</summary>
public class PricingTier
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string Name { get; set; } = default!;
    public decimal Price { get; set; }
    public int AllocatedQuantity { get; set; }
    public int SoldQuantity { get; set; }

    public Event? Event { get; set; }
}

/// <summary>Represents a customer order.</summary>
public class Order
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public string IdempotencyKey { get; set; } = default!;
    public string CustomerRef { get; set; } = default!;
    public decimal TotalAmount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public Event? Event { get; set; }
    public List<OrderLine> Lines { get; set; } = new();
}

/// <summary>Represents a line item within an order.</summary>
public class OrderLine
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid PricingTierId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }

    public Order? Order { get; set; }
    public PricingTier? PricingTier { get; set; }
}
