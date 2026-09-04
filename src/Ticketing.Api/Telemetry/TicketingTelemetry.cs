using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace Ticketing.Api.Telemetry;

/// <summary>
/// Central place for the application's custom ActivitySource and Meter instruments so that
/// OpenTelemetry configuration and instrumented code reference the same identities.
/// </summary>
public static class TicketingTelemetry
{
    public const string ServiceName = "ticketing-api";
    public const string ActivitySourceName = "Ticketing.Purchases";
    public const string MeterName = "Ticketing.Api";

    public static readonly string ServiceVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, ServiceVersion);

    private static readonly Meter Meter = new(MeterName, ServiceVersion);

    public static readonly Counter<long> TicketsPurchased =
        Meter.CreateCounter<long>("tickets.purchased", unit: "{ticket}", description: "Number of tickets successfully purchased.");

    public static readonly Counter<long> OversellAttempts =
        Meter.CreateCounter<long>("purchase.oversell_attempts", unit: "{attempt}", description: "Number of times an atomic inventory claim affected zero rows.");

    public static readonly Counter<long> IdempotentReplays =
        Meter.CreateCounter<long>("purchase.idempotent_replays", unit: "{replay}", description: "Number of purchase requests replayed via idempotency key.");

    public static readonly Histogram<double> PurchaseDuration =
        Meter.CreateHistogram<double>("purchase.duration", unit: "ms", description: "Duration of purchase attempts in milliseconds.");

    public static readonly UpDownCounter<long> TicketsRemaining =
        Meter.CreateUpDownCounter<long>("tickets.remaining", unit: "{ticket}", description: "Remaining tickets for a pricing tier.");
}
