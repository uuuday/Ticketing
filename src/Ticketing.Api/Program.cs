using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using Ticketing.Api.Data;
using Ticketing.Api.Middleware;
using Ticketing.Api.Services;
using Ticketing.Api.Telemetry;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// ---- Serilog structured logging ----
builder.Host.UseSerilog((context, services, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithEnvironmentName()
        .Enrich.With<ActivityEnricher>()
        .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter());
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Ticketing API",
        Version = "v1"
    });
});

//builder.Services.AddSwaggerGen(c =>
//{
//    c.OperationFilter<IdempotencyKeyHeaderFilter>();
//});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddHttpContextAccessor();

builder.Services.AddDbContext<TicketingDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Ticketing")
        ?? "Data Source=DESKTOP-63DDF7F\\SQLEXPRESS;Initial Catalog=Ticketing;Integrated Security=True;Trust Server Certificate=True"));
builder.Services.AddScoped<IPurchaseService, PurchaseService>();

builder.Services.AddHealthChecks()
    .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddDbContextCheck<TicketingDbContext>("database", tags: new[] { "ready" });

// ---- OpenTelemetry ----
var otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
var useConsoleExporter = builder.Configuration.GetValue<bool>("OpenTelemetry:UseConsoleExporter");

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource
        .AddService(serviceName: TicketingTelemetry.ServiceName, serviceVersion: TicketingTelemetry.ServiceVersion))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(TicketingTelemetry.ActivitySourceName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSqlClientInstrumentation(options =>
            {
                // Attempt to set a development-only flag to capture SQL text/parameters when
                // the instrumentation package exposes the option. Different versions
                // surface different property names, so use reflection and silently fall
                // back to defaults when the property is not present.
                try
                {
                    var t = options.GetType();
                    var propNames = new[] { "SetDbStatementForText", "SetDbQueryParameters", "SetDbStatementForTextOnly" };
                    foreach (var name in propNames)
                    {
                        var p = t.GetProperty(name);
                        if (p != null && p.PropertyType == typeof(bool) && p.CanWrite)
                        {
                            p.SetValue(options, builder.Environment.IsDevelopment());
                            break;
                        }
                    }
                }
                catch
                {
                    // Ignore: instrumentation will use its defaults
                }
            });

        if (useConsoleExporter)
        {
            tracing.AddConsoleExporter();
        }

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            tracing.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint));
        }
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(TicketingTelemetry.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation();

        if (useConsoleExporter)
        {
            metrics.AddConsoleExporter();
        }

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            metrics.AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otlpEndpoint));
        }
    });

var app = builder.Build();

app.UseExceptionHandler(_ => { });

app.UseMiddleware<CorrelationIdMiddleware>();

app.Use(async (context, next) =>
{
    context.Items["RequestStopwatch"] = Stopwatch.StartNew();
    await next();
});

app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {ElapsedMs:0.0000} ms";

    options.GetLevel = (httpContext, elapsed, ex) =>
    {
        if (ex is not null || httpContext.Response.StatusCode >= 500)
        {
            return LogEventLevel.Error;
        }

        if (httpContext.Response.StatusCode >= 400)
        {
            return LogEventLevel.Warning;
        }

        return LogEventLevel.Information;
    };

    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        var stopwatch = httpContext.Items["RequestStopwatch"] as Stopwatch;
        diagnosticContext.Set("RequestPath", httpContext.Request.Path.Value ?? string.Empty);
        diagnosticContext.Set("StatusCode", httpContext.Response.StatusCode);
        diagnosticContext.Set("ElapsedMs", stopwatch?.Elapsed.TotalMilliseconds ?? 0d);
        diagnosticContext.Set("ClientIp", httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    };
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Ticketing API v1");
    });
}

app.UseHttpsRedirection();
app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live"),
    ResponseWriter = WriteHealthCheckResponse
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = WriteHealthCheckResponse
});

app.Run();

static async Task WriteHealthCheckResponse(HttpContext context, Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport report)
{
    context.Response.ContentType = "application/json";

    var payload = new
    {
        status = report.Status.ToString(),
        totalDurationMs = report.TotalDuration.TotalMilliseconds,
        checks = report.Entries.Select(entry => new
        {
            name = entry.Key,
            status = entry.Value.Status.ToString(),
            durationMs = entry.Value.Duration.TotalMilliseconds,
            description = entry.Value.Description
        })
    };

    await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
}

/// <summary>Enriches every log event with the current Activity's TraceId and SpanId, when available.</summary>
public class ActivityEnricher : Serilog.Core.ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, Serilog.Core.ILogEventPropertyFactory propertyFactory)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("TraceId", activity.TraceId.ToString()));
        logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("SpanId", activity.SpanId.ToString()));
    }
}

/// <summary>Entry point partial class exposed for WebApplicationFactory in tests.</summary>
public partial class Program { }
