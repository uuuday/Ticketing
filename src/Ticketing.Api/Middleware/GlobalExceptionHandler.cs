using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ticketing.Api.Domain;

namespace Ticketing.Api.Middleware;

/// <summary>
/// Central, last-resort exception handler. Maps known exception types to RFC 7807
/// ProblemDetails responses, logs at the appropriate level, and never leaks stack traces or
/// inner exception detail to the client.
/// </summary>
public class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (statusCode, title) = MapException(exception);

        var correlationId = httpContext.Items.TryGetValue(CorrelationIdMiddleware.HttpContextItemKey, out var value)
            ? value?.ToString()
            : null;
        var traceId = Activity.Current?.TraceId.ToString() ?? httpContext.TraceIdentifier;

        if (statusCode >= 500)
        {
            _logger.LogError(exception,
                "Unhandled exception processing {RequestMethod} {RequestPath}. CorrelationId {CorrelationId}",
                httpContext.Request.Method, httpContext.Request.Path, correlationId);
        }
        else if (exception is OperationCanceledException)
        {
            _logger.LogInformation(
                "Request {RequestMethod} {RequestPath} was cancelled by the client. CorrelationId {CorrelationId}",
                httpContext.Request.Method, httpContext.Request.Path, correlationId);
        }
        else
        {
            _logger.LogWarning(
                "Request {RequestMethod} {RequestPath} failed with {StatusCode}: {ExceptionMessage}. CorrelationId {CorrelationId}",
                httpContext.Request.Method, httpContext.Request.Path, statusCode, exception.Message, correlationId);
        }

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = statusCode >= 500 ? "An unexpected error occurred." : exception.Message,
        };
        problemDetails.Extensions["correlationId"] = correlationId;
        problemDetails.Extensions["traceId"] = traceId;

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);

        return true;
    }

    private static (int StatusCode, string Title) MapException(Exception exception) => exception switch
    {
        ValidationException => (422, "Unprocessable Entity"),
        NotFoundException => (404, "Not Found"),
        ConflictException => (409, "Conflict"),
        DbUpdateConcurrencyException => (409, "Conflict"),
        OperationCanceledException => (499, "Client Closed Request"),
        _ => (500, "Internal Server Error")
    };
}
