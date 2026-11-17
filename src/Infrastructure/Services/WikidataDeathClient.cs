using System.Net;
using AreTheyDeadOrNot.Core.Services;

namespace AreTheyDeadOrNot.Infrastructure.Services;

public sealed class WikidataRateLimitException(TimeSpan retryAfter)
    : Exception($"Wikidata rate-limited request; retry after {retryAfter.TotalSeconds:F0}s.")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

public sealed class WikidataDeathClient(HttpClient httpClient)
{
    public async Task<Dictionary<string, DateOnly>> FetchDeathDatesAsync(
        IReadOnlyList<string> qids,
        CancellationToken cancellationToken = default)
    {
        if (qids.Count == 0)
        {
            return [];
        }

        // wbgetentities accepts up to 50 IDs per request for standard clients
        var joinedIds = string.Join('|', qids.Take(50));
        var url = $"https://www.wikidata.org/w/api.php?action=wbgetentities&ids={Uri.EscapeDataString(joinedIds)}&props=claims&format=json";

        using var response = await httpClient.GetAsync(url, cancellationToken);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
            throw new WikidataRateLimitException(retryAfter);
        }

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return WikidataDeathParser.ParseDeathDates(json);
    }
}
