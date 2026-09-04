using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ticketing.Api.Contracts;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Ticketing.Api.Services;
using Xunit;

namespace Ticketing.Tests;

public class PurchaseServiceTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public PurchaseServiceTests(TestWebAppFactory factory)
    {
        _factory = factory;
    }

    private (IServiceScope Scope, TicketingDbContext Db, IPurchaseService Service) CreateScope()
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPurchaseService>();
        return (scope, db, service);
    }

    private static Event NewEvent(DateTimeOffset when, bool cancelled = false, int capacity = 100) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test Event",
        Venue = "Test Venue",
        EventDateTime = when,
        TotalCapacity = capacity,
        IsCancelled = cancelled,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static PricingTier NewTier(Guid eventId, int allocated, decimal price = 10m) => new()
    {
        Id = Guid.NewGuid(),
        EventId = eventId,
        Name = "General",
        Price = price,
        AllocatedQuantity = allocated,
        SoldQuantity = 0
    };

    [Fact]
    public async Task Purchase_WithQuantityLessThanOne_ReturnsInvalid()
    {
        var (scope, db, service) = CreateScope();
        using (scope)
        {
            var ev = NewEvent(DateTimeOffset.UtcNow.AddDays(1));
            var tier = NewTier(ev.Id, 10);
            ev.PricingTiers.Add(tier);
            db.Events.Add(ev);
            await db.SaveChangesAsync();

            var request = new PurchaseRequest(ev.Id, "cust-1", new List<PurchaseLineRequest> { new(tier.Id, 0) });
            var result = await service.PurchaseAsync(request, Guid.NewGuid().ToString(), CancellationToken.None);

            result.Status.Should().Be(PurchaseStatus.Invalid);
        }
    }

    [Fact]
    public async Task Purchase_WithUnknownTier_ReturnsNotFound()
    {
        var (scope, db, service) = CreateScope();
        using (scope)
        {
            var ev = NewEvent(DateTimeOffset.UtcNow.AddDays(1));
            db.Events.Add(ev);
            await db.SaveChangesAsync();

            var request = new PurchaseRequest(ev.Id, "cust-1", new List<PurchaseLineRequest> { new(Guid.NewGuid(), 1) });
            var result = await service.PurchaseAsync(request, Guid.NewGuid().ToString(), CancellationToken.None);

            result.Status.Should().Be(PurchaseStatus.NotFound);
        }
    }

    [Fact]
    public async Task Purchase_ForCancelledEvent_ReturnsConflict()
    {
        var (scope, db, service) = CreateScope();
        using (scope)
        {
            var ev = NewEvent(DateTimeOffset.UtcNow.AddDays(1), cancelled: true);
            var tier = NewTier(ev.Id, 10);
            ev.PricingTiers.Add(tier);
            db.Events.Add(ev);
            await db.SaveChangesAsync();

            var request = new PurchaseRequest(ev.Id, "cust-1", new List<PurchaseLineRequest> { new(tier.Id, 1) });
            var result = await service.PurchaseAsync(request, Guid.NewGuid().ToString(), CancellationToken.None);

            result.Status.Should().Be(PurchaseStatus.Conflict);
        }
    }

    [Fact]
    public async Task Purchase_ForPastEvent_ReturnsConflict()
    {
        var (scope, db, service) = CreateScope();
        using (scope)
        {
            var ev = NewEvent(DateTimeOffset.UtcNow.AddDays(-1));
            var tier = NewTier(ev.Id, 10);
            ev.PricingTiers.Add(tier);
            db.Events.Add(ev);
            await db.SaveChangesAsync();

            var request = new PurchaseRequest(ev.Id, "cust-1", new List<PurchaseLineRequest> { new(tier.Id, 1) });
            var result = await service.PurchaseAsync(request, Guid.NewGuid().ToString(), CancellationToken.None);

            result.Status.Should().Be(PurchaseStatus.Conflict);
        }
    }

    [Fact]
    public async Task Purchase_WithDuplicateTierInRequest_ReturnsInvalid()
    {
        var (scope, db, service) = CreateScope();
        using (scope)
        {
            var ev = NewEvent(DateTimeOffset.UtcNow.AddDays(1));
            var tier = NewTier(ev.Id, 10);
            ev.PricingTiers.Add(tier);
            db.Events.Add(ev);
            await db.SaveChangesAsync();

            var request = new PurchaseRequest(ev.Id, "cust-1", new List<PurchaseLineRequest>
            {
                new(tier.Id, 1),
                new(tier.Id, 2)
            });
            var result = await service.PurchaseAsync(request, Guid.NewGuid().ToString(), CancellationToken.None);

            result.Status.Should().Be(PurchaseStatus.Invalid);
        }
    }

    [Fact]
    public async Task Purchase_WithMultipleTiers_ComputesTotalsCorrectly()
    {
        var (scope, db, service) = CreateScope();
        using (scope)
        {
            var ev = NewEvent(DateTimeOffset.UtcNow.AddDays(1), capacity: 100);
            var tierA = NewTier(ev.Id, 10, price: 20m);
            var tierB = NewTier(ev.Id, 10, price: 35.50m);
            ev.PricingTiers.Add(tierA);
            ev.PricingTiers.Add(tierB);
            db.Events.Add(ev);
            await db.SaveChangesAsync();

            var request = new PurchaseRequest(ev.Id, "cust-1", new List<PurchaseLineRequest>
            {
                new(tierA.Id, 2),
                new(tierB.Id, 3)
            });
            var result = await service.PurchaseAsync(request, Guid.NewGuid().ToString(), CancellationToken.None);

            result.Status.Should().Be(PurchaseStatus.Created);
            result.Order.Should().NotBeNull();
            result.Order!.TotalAmount.Should().Be((2 * 20m) + (3 * 35.50m));
            result.Order.Lines.Should().HaveCount(2);
        }
    }
}
