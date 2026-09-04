using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Ticketing.Api.Contracts;
using Xunit;

namespace Ticketing.Tests;

public class EventsControllerTests : IClassFixture<TestWebAppFactory>
{
    private readonly HttpClient _client;

    public EventsControllerTests(TestWebAppFactory factory)
    {
        _client = factory.CreateClient();
    }

    private static CreateEventRequest ValidEventRequest() => new(
        "Concert",
        "A great concert",
        "Arena",
        DateTimeOffset.UtcNow.AddDays(30),
        100,
        new List<CreatePricingTierRequest> { new("General", 25m, 50) });

    [Fact]
    public async Task CreateEvent_ReturnsCreatedWithLocation()
    {
        var response = await _client.PostAsJsonAsync("/api/events", ValidEventRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();

        var dto = await response.Content.ReadFromJsonAsync<EventDto>();
        dto.Should().NotBeNull();
        dto!.Name.Should().Be("Concert");
    }

    [Fact]
    public async Task CreateEvent_WithTierAllocationExceedingCapacity_ReturnsUnprocessableEntity()
    {
        var request = new CreateEventRequest(
            "Concert",
            null,
            "Arena",
            DateTimeOffset.UtcNow.AddDays(30),
            10,
            new List<CreatePricingTierRequest> { new("General", 25m, 20) });

        var response = await _client.PostAsJsonAsync("/api/events", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task CreateEvent_WithPastDate_ReturnsUnprocessableEntity()
    {
        var request = new CreateEventRequest(
            "Concert",
            null,
            "Arena",
            DateTimeOffset.UtcNow.AddDays(-1),
            100,
            new List<CreatePricingTierRequest> { new("General", 25m, 50) });

        var response = await _client.PostAsJsonAsync("/api/events", request);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task GetEvent_WithUnknownId_ReturnsNotFound()
    {
        var response = await _client.GetAsync($"/api/events/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteEvent_WithExistingOrders_SoftCancelsRatherThanRemoving()
    {
        var createResponse = await _client.PostAsJsonAsync("/api/events", ValidEventRequest());
        var created = await createResponse.Content.ReadFromJsonAsync<EventDto>();
        created.Should().NotBeNull();

        var tierId = created!.PricingTiers[0].Id;
        var idempotencyKey = Guid.NewGuid().ToString();
        var purchaseRequest = new PurchaseRequest(created.Id, "cust-1", new List<PurchaseLineRequest> { new(tierId, 1) });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/purchases")
        {
            Content = JsonContent.Create(purchaseRequest)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        var purchaseResponse = await _client.SendAsync(request);
        purchaseResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var deleteResponse = await _client.DeleteAsync($"/api/events/{created.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await _client.GetAsync($"/api/events/{created.Id}");
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var eventAfterDelete = await getResponse.Content.ReadFromJsonAsync<EventDto>();
        eventAfterDelete!.IsCancelled.Should().BeTrue();
    }
}
