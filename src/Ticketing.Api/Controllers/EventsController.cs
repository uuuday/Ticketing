using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Contracts;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Controllers;

/// <summary>Manages events and their pricing tiers.</summary>
[ApiController]
[Route("api/events")]
public class EventsController : ControllerBase
{
    private readonly TicketingDbContext _db;
    private readonly ILogger<EventsController> _logger;

    public EventsController(TicketingDbContext db, ILogger<EventsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Creates a new event with its pricing tiers.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateEvent([FromBody] CreateEventRequest request, CancellationToken cancellationToken)
    {
        if (request.EventDateTime <= DateTimeOffset.UtcNow)
        {
            throw new ValidationException("Event date must be in the future.");
        }

        var totalAllocated = request.PricingTiers.Sum(t => t.AllocatedQuantity);
        if (totalAllocated > request.TotalCapacity)
        {
            throw new ValidationException("Sum of tier allocations exceeds total capacity.");
        }

        var newEvent = new Event
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            Venue = request.Venue,
            EventDateTime = request.EventDateTime,
            TotalCapacity = request.TotalCapacity,
            IsCancelled = false,
            CreatedAt = DateTimeOffset.UtcNow,
            PricingTiers = request.PricingTiers.Select(t => new PricingTier
            {
                Id = Guid.NewGuid(),
                Name = t.Name,
                Price = t.Price,
                AllocatedQuantity = t.AllocatedQuantity,
                SoldQuantity = 0
            }).ToList()
        };

        _db.Events.Add(newEvent);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Event {EventId} created with {TierCount} pricing tiers", newEvent.Id, newEvent.PricingTiers.Count);

        var dto = ToDto(newEvent);
        return CreatedAtAction(nameof(GetEvent), new { id = newEvent.Id }, dto);
    }

    /// <summary>Gets a paged list of events.</summary>
    [HttpGet]
    public async Task<IActionResult> GetEvents([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken cancellationToken = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = Math.Clamp(pageSize, 1, 100);

        var totalCount = await _db.Events.CountAsync(cancellationToken);
        var events = await _db.Events
            .Include(e => e.PricingTiers)
            .OrderBy(e => e.EventDateTime)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var result = new PagedResult<EventDto>(events.Select(ToDto).ToList(), page, pageSize, totalCount);
        return Ok(result);
    }

    /// <summary>Gets a single event by id.</summary>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetEvent(Guid id, CancellationToken cancellationToken)
    {
        var ev = await _db.Events.Include(e => e.PricingTiers).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (ev is null)
        {
            throw new NotFoundException("Event not found.");
        }

        return Ok(ToDto(ev));
    }

    /// <summary>Updates event metadata (name, description, venue, date, capacity). Does not modify pricing tiers.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateEvent(Guid id, [FromBody] UpdateEventRequest request, CancellationToken cancellationToken)
    {
        var ev = await _db.Events.Include(e => e.PricingTiers).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (ev is null)
        {
            throw new NotFoundException("Event not found.");
        }

        if (request.EventDateTime <= DateTimeOffset.UtcNow)
        {
            throw new ValidationException("Event date must be in the future.");
        }

        var totalAllocated = ev.PricingTiers.Sum(t => t.AllocatedQuantity);
        if (totalAllocated > request.TotalCapacity)
        {
            throw new ConflictException("Total capacity conflicts with existing tier allocations.");
        }

        ev.Name = request.Name;
        ev.Description = request.Description;
        ev.Venue = request.Venue;
        ev.EventDateTime = request.EventDateTime;
        ev.TotalCapacity = request.TotalCapacity;
        ev.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(ToDto(ev));
    }

    /// <summary>Deletes an event. Hard-deletes if no orders exist, otherwise soft-cancels.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteEvent(Guid id, CancellationToken cancellationToken)
    {
        var ev = await _db.Events.Include(e => e.PricingTiers).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (ev is null)
        {
            throw new NotFoundException("Event not found.");
        }

        var hasOrders = await _db.Orders.AnyAsync(o => o.EventId == id, cancellationToken);
        if (hasOrders)
        {
            ev.IsCancelled = true;
            ev.UpdatedAt = DateTimeOffset.UtcNow;
            _logger.LogInformation("Event {EventId} cancelled (soft-delete) because existing orders were found", id);
        }
        else
        {
            _db.Events.Remove(ev);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>Gets ticket availability per tier for an event.</summary>
    [HttpGet("{id:guid}/availability")]
    public async Task<IActionResult> GetAvailability(Guid id, CancellationToken cancellationToken)
    {
        var ev = await _db.Events.Include(e => e.PricingTiers).FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
        if (ev is null)
        {
            throw new NotFoundException("Event not found.");
        }

        var tiers = ev.PricingTiers
            .Select(t => new TierAvailabilityDto(t.Id, t.Name, t.AllocatedQuantity, t.SoldQuantity, t.AllocatedQuantity - t.SoldQuantity))
            .ToList();

        return Ok(new EventAvailabilityDto(ev.Id, tiers));
    }

    private static EventDto ToDto(Event e) => new(
        e.Id,
        e.Name,
        e.Description,
        e.Venue,
        e.EventDateTime,
        e.TotalCapacity,
        e.IsCancelled,
        e.CreatedAt,
        e.UpdatedAt,
        e.PricingTiers.Select(t => new PricingTierDto(t.Id, t.Name, t.Price, t.AllocatedQuantity, t.SoldQuantity)).ToList());
}
