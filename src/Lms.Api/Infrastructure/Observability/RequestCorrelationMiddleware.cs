using System.Diagnostics;

namespace Lms.Api.Infrastructure.Observability;

public sealed class RequestCorrelationMiddleware(RequestDelegate next, RequestMetrics metrics)
{
    public async Task InvokeAsync(HttpContext httpContext)
    {
        var correlationId = httpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault();
        if (!Guid.TryParse(correlationId, out _))
        {
            correlationId = Guid.NewGuid().ToString("D");
        }

        httpContext.Items["CorrelationId"] = correlationId;
        httpContext.Response.Headers["X-Correlation-Id"] = correlationId;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(httpContext);
        }
        finally
        {
            metrics.Record(httpContext, stopwatch.Elapsed);
        }
    }
}
