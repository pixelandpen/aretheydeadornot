using System.Diagnostics;
using System.Threading.RateLimiting;
using AreTheyDeadOrNot.Core.Models;
using AreTheyDeadOrNot.Core.Repositories;
using AreTheyDeadOrNot.Core.Services;
using AreTheyDeadOrNot.Infrastructure;
using AreTheyDeadOrNot.Infrastructure.Data;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Hybrid;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var permitLimit = builder.Configuration.GetValue("RateLimiting:PermitLimit", 60);
var windowSeconds = builder.Configuration.GetValue("RateLimiting:WindowSeconds", 10);

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                Math.Ceiling(retryAfter.TotalSeconds).ToString();
        }

        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Rate limit exceeded. Please slow down." },
            cancellationToken: token);
    };

    options.AddPolicy("api-per-ip", httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetSlidingWindowLimiter(ip, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(windowSeconds),
            SegmentsPerWindow = 5,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        });
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseForwardedHeaders();
app.UseCors();
app.UseRateLimiter();

// Kubernetes liveness & readiness probes
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));

app.MapGet("/health/ready", async (ReadReplicaDataSource replica, CancellationToken ct) =>
{
    try
    {
        await using var conn = await replica.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Read replica unavailable",
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

var v1 = app.MapGroup("/api/v1").RequireRateLimiting("api-per-ip");

v1.MapGet("/search", async (
    string? name,
    IPersonRepository repository,
    DisambiguationService disambiguation,
    HybridCache cache,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();
    var normalized = NameNormalizer.Normalize(name);

    if (string.IsNullOrEmpty(normalized))
    {
        return Results.BadRequest(new { error = "Query parameter 'name' is required." });
    }

    var cacheKey = $"search:{normalized}";
    var cached = await cache.GetOrCreateAsync(
        cacheKey,
        async cancel =>
        {
            var matches = await repository.SearchByNameAsync(normalized, limit: 5, cancel);
            var (primary, didYouMean) = disambiguation.SelectBestMatch(matches);
            return new CachedSearchResult(
                primary is not null,
                primary is null ? null : PersonDto.FromRecord(primary),
                didYouMean);
        },
        cancellationToken: ct);

    sw.Stop();
    httpContext.Response.Headers["X-Query-Time-Ms"] = sw.ElapsedMilliseconds.ToString();

    return Results.Ok(new SearchResponse(
        name!.Trim(),
        cached.Found,
        cached.Person,
        cached.DidYouMean,
        sw.ElapsedMilliseconds));
});

v1.MapGet("/person/{qid}", async (
    string qid,
    IPersonRepository repository,
    HybridCache cache,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();
    var cleanQid = qid.Trim().ToUpperInvariant();

    var person = await cache.GetOrCreateAsync(
        $"person:{cleanQid}",
        async cancel =>
        {
            var record = await repository.GetByQidAsync(cleanQid, cancel);
            return record is null ? null : PersonDto.FromRecord(record);
        },
        cancellationToken: ct);

    sw.Stop();
    httpContext.Response.Headers["X-Query-Time-Ms"] = sw.ElapsedMilliseconds.ToString();

    return person is null
        ? Results.NotFound(new { error = $"Person '{cleanQid}' not found." })
        : Results.Ok(new SearchResponse(person.Name, true, person, [], sw.ElapsedMilliseconds));
});

v1.MapGet("/recent-deaths", async (
    int? limit,
    IPersonRepository repository,
    HybridCache cache,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();
    var clampedLimit = Math.Clamp(limit ?? 12, 1, 50);

    var items = await cache.GetOrCreateAsync(
        $"recent-deaths:{clampedLimit}",
        async cancel =>
        {
            var records = await repository.GetRecentDeathsAsync(clampedLimit, cancel);
            return records.Select(r => PersonDto.FromRecord(r)).ToList();
        },
        new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromMinutes(15),
            LocalCacheExpiration = TimeSpan.FromMinutes(5)
        },
        cancellationToken: ct);

    sw.Stop();
    httpContext.Response.Headers["X-Query-Time-Ms"] = sw.ElapsedMilliseconds.ToString();
    return Results.Ok(new { count = items.Count, queryTimeMs = sw.ElapsedMilliseconds, items });
});

v1.MapGet("/in-memory", async (
    int? days,
    int? limit,
    IPersonRepository repository,
    HybridCache cache,
    HttpContext httpContext,
    CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();
    var radius = Math.Clamp(days ?? 3, 1, 7);
    var clampedLimit = Math.Clamp(limit ?? 12, 1, 50);
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    var keys = InMemoryWindowCalculator.GetMonthDayKeys(today, radius);

    var items = await cache.GetOrCreateAsync(
        $"in-memory:{today:yyyy-MM-dd}:d{radius}:l{clampedLimit}",
        async cancel =>
        {
            var records = await repository.GetInMemoryAsync(keys, clampedLimit, cancel);
            return records.Select(r => PersonDto.FromRecord(r, today)).ToList();
        },
        new HybridCacheEntryOptions
        {
            Expiration = TimeSpan.FromHours(6),
            LocalCacheExpiration = TimeSpan.FromMinutes(30)
        },
        cancellationToken: ct);

    sw.Stop();
    httpContext.Response.Headers["X-Query-Time-Ms"] = sw.ElapsedMilliseconds.ToString();
    return Results.Ok(new { date = today, radiusDays = radius, count = items.Count, queryTimeMs = sw.ElapsedMilliseconds, items });
});

app.Run();

internal sealed record CachedSearchResult(
    bool Found,
    PersonDto? Person,
    IReadOnlyList<DidYouMeanCandidate> DidYouMean);
