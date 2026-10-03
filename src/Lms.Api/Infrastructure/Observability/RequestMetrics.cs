using System.Collections.Concurrent;
using System.Diagnostics;

namespace Lms.Api.Infrastructure.Observability;

public sealed class RequestMetrics
{
    private readonly ConcurrentDictionary<string, RouteMetric> routes = new(StringComparer.OrdinalIgnoreCase);
    private long totalRequests;
    private long failedRequests;

    public void Record(HttpContext httpContext, TimeSpan elapsed)
    {
        var route = $"{httpContext.Request.Method} {NormalizePath(httpContext.Request.Path)}";
        var metric = routes.GetOrAdd(route, _ => new RouteMetric());
        metric.Record(httpContext.Response.StatusCode, elapsed);
        Interlocked.Increment(ref totalRequests);
        if (httpContext.Response.StatusCode >= 400) Interlocked.Increment(ref failedRequests);
    }

    public RequestMetricsSnapshot Snapshot()
    {
        var routeSnapshots = routes
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Value.Snapshot(item.Key))
            .ToArray();
        return new RequestMetricsSnapshot(
            DateTimeOffset.UtcNow,
            Volatile.Read(ref totalRequests),
            Volatile.Read(ref failedRequests),
            routeSnapshots);
    }

    private static string NormalizePath(PathString path)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        for (var index = 0; index < segments.Length; index++)
        {
            if (Guid.TryParse(segments[index], out _) || long.TryParse(segments[index], out _) || segments[index].Length > 64)
                segments[index] = "{id}";
        }
        return segments.Length == 0 ? "/" : $"/{string.Join('/', segments)}";
    }

    private sealed class RouteMetric
    {
        private long count;
        private long failed;
        private long totalMilliseconds;
        private int lastStatusCode;
        private long lastRequestUnixMilliseconds;

        public void Record(int statusCode, TimeSpan elapsed)
        {
            Interlocked.Increment(ref count);
            if (statusCode >= 400) Interlocked.Increment(ref failed);
            Interlocked.Add(ref totalMilliseconds, Math.Max(0, (long)elapsed.TotalMilliseconds));
            Volatile.Write(ref lastStatusCode, statusCode);
            Volatile.Write(ref lastRequestUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public RouteMetricSnapshot Snapshot(string route) => new(
            route,
            Volatile.Read(ref count),
            Volatile.Read(ref failed),
            Volatile.Read(ref totalMilliseconds),
            Volatile.Read(ref count) == 0 ? 0 : Math.Round((double)Volatile.Read(ref totalMilliseconds) / Volatile.Read(ref count), 2),
            Volatile.Read(ref lastStatusCode),
            DateTimeOffset.FromUnixTimeMilliseconds(Volatile.Read(ref lastRequestUnixMilliseconds)));
    }
}

public sealed record RequestMetricsSnapshot(
    DateTimeOffset GeneratedAtUtc,
    long TotalRequests,
    long FailedRequests,
    IReadOnlyCollection<RouteMetricSnapshot> Routes);

public sealed record RouteMetricSnapshot(
    string Route,
    long Count,
    long Failed,
    long TotalMilliseconds,
    double AverageMilliseconds,
    int LastStatusCode,
    DateTimeOffset LastRequestUtc);
