using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Contracts;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Controllers;

/// <summary>Provides sales reporting for events.</summary>
[ApiController]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly TicketingDbContext _db;

    public ReportsController(TicketingDbContext db)
    {
        _db = db;
    }

    /// <summary>Gets units sold and revenue per pricing tier for an event, aggregated in SQL.</summary>
    [HttpGet("sales")]
    public async Task<IActionResult> GetSalesReport([FromQuery] Guid eventId, CancellationToken cancellationToken)
    {
        var eventExists = await _db.Events.AnyAsync(e => e.Id == eventId, cancellationToken);
        if (!eventExists)
        {
            throw new NotFoundException("Event not found.");
        }

        var tierSales = await (
            from tier in _db.PricingTiers
            where tier.EventId == eventId
            join line in _db.OrderLines on tier.Id equals line.PricingTierId into lines
            select new TierSalesDto(
                tier.Id,
                tier.Name,
                lines.Sum(l => (int?)l.Quantity) ?? 0,
                lines.Sum(l => (decimal?)(l.Quantity * l.UnitPrice)) ?? 0m))
            .ToListAsync(cancellationToken);

        var report = new SalesReportDto(
            eventId,
            tierSales,
            tierSales.Sum(t => t.UnitsSold),
            tierSales.Sum(t => t.Revenue));

        return Ok(report);
    }
}
