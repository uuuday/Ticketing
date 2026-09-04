using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ticketing.Api.Contracts;
using Ticketing.Api.Data;
using Ticketing.Api.Domain;
using Xunit;

namespace Ticketing.Tests;

public class ConcurrencyTests : IClassFixture<TestWebAppFactory>
{
    private readonly TestWebAppFactory _factory;

    public ConcurrencyTests(TestWebAppFactory factory)
    {
        _factory = factory;
    }

    private async Task<(Guid EventId, Guid TierId)> SeedEventWithTierAsync(int allocatedQuantity)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();

        var ev = new Event
        {
            Id = Guid.NewGuid(),
            Name = "Concurrency Test Event",
            Venue = "Arena",
            EventDateTime = DateTimeOffset.UtcNow.AddDays(30),
            TotalCapacity = allocatedQuantity,
            IsCancelled = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var tier = new PricingTier
        {
            Id = Guid.NewGuid(),
            EventId = ev.Id,
            Name = "General",
            Price = 10m,
            AllocatedQuantity = allocatedQuantity,
            SoldQuantity = 0
        };
        ev.PricingTiers.Add(tier);

        db.Events.Add(ev);
        await db.SaveChangesAsync();

        return (ev.Id, tier.Id);
    }

    private async Task<HttpResponseMessage> SendPurchaseAsync(HttpClient client, Guid eventId, Guid tierId, string idempotencyKey, int quantity = 1)
    {
        var request = new PurchaseRequest(eventId, "cust", new List<PurchaseLineRequest> { new(tierId, quantity) });
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/purchases")
        {
            Content = JsonContent.Create(request)
        };
        httpRequest.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(httpRequest);
    }

    [Fact]
    public async Task Purchase_100ParallelRequests_ExactlyAllocatedQuantitySucceed()
    {
        const int allocated = 10;
        const int attempts = 100;
        var (eventId, tierId) = await SeedEventWithTierAsync(allocated);
        var client = _factory.CreateClient();

        var tasks = Enumerable.Range(0, attempts)
            .Select(i => SendPurchaseAsync(client, eventId, tierId, $"key-{Guid.NewGuid()}"))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        var created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var conflicts = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        created.Should().Be(allocated);
        conflicts.Should().Be(attempts - allocated);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        var tier = await db.PricingTiers.AsNoTracking().FirstAsync(t => t.Id == tierId);
        tier.SoldQuantity.Should().Be(allocated);
    }

    [Fact]
    public async Task Purchase_SameIdempotencyKeySentTwiceSequentially_OnlyClaimsOnce()
    {
        var (eventId, tierId) = await SeedEventWithTierAsync(10);
        var client = _factory.CreateClient();
        var key = Guid.NewGuid().ToString();

        var first = await SendPurchaseAsync(client, eventId, tierId, key, quantity: 3);
        var second = await SendPurchaseAsync(client, eventId, tierId, key, quantity: 3);

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        var tier = await db.PricingTiers.AsNoTracking().FirstAsync(t => t.Id == tierId);
        tier.SoldQuantity.Should().Be(3);
    }

    [Fact]
    public async Task Purchase_SameIdempotencyKeySentTwiceInParallel_ExactlyOneOrderExists()
    {
        var (eventId, tierId) = await SeedEventWithTierAsync(10);
        var client = _factory.CreateClient();
        var key = Guid.NewGuid().ToString();

        var task1 = SendPurchaseAsync(client, eventId, tierId, key, quantity: 2);
        var task2 = SendPurchaseAsync(client, eventId, tierId, key, quantity: 2);

        var responses = await Task.WhenAll(task1, task2);

        responses.Should().Contain(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketingDbContext>();
        var orderCount = await db.Orders.CountAsync(o => o.IdempotencyKey == key);
        orderCount.Should().Be(1);
    }
}
