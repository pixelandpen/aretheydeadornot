using AreTheyDeadOrNot.Core.Models;

namespace AreTheyDeadOrNot.Core.Services;

public sealed record DisambiguationOptions(
    double RelativeSitelinkThreshold = 0.20,
    int CloseSitelinkGap = 10,
    int MaxAlternatives = 2);

public sealed class DisambiguationService
{
    private readonly DisambiguationOptions _options;

    public DisambiguationService(DisambiguationOptions? options = null)
    {
        _options = options ?? new DisambiguationOptions();
    }

    public (PersonRecord? Primary, IReadOnlyList<DidYouMeanCandidate> DidYouMean) SelectBestMatch(
        IEnumerable<PersonRecord> candidates)
    {
        var ordered = candidates
            .OrderByDescending(c => c.Sitelinks)
            .ThenBy(c => c.Qid, StringComparer.Ordinal)
            .ToList();

        if (ordered.Count == 0)
        {
            return (null, []);
        }

        var primary = ordered[0];
        var alternatives = new List<DidYouMeanCandidate>(_options.MaxAlternatives);

        for (var i = 1; i < ordered.Count && alternatives.Count < _options.MaxAlternatives; i++)
        {
            var candidate = ordered[i];
            if (IsCloseCandidate(primary.Sitelinks, candidate.Sitelinks))
            {
                alternatives.Add(new DidYouMeanCandidate(
                    candidate.Qid,
                    candidate.Name,
                    candidate.IsDead,
                    candidate.BirthDate,
                    candidate.DeathDate,
                    candidate.Sitelinks,
                    candidate.Occupations));
            }
        }

        return (primary, alternatives);
    }

    private bool IsCloseCandidate(int topSitelinks, int candidateSitelinks)
    {
        if (topSitelinks <= 0)
        {
            return true;
        }

        if (topSitelinks - candidateSitelinks <= _options.CloseSitelinkGap)
        {
            return true;
        }

        var ratio = (double)candidateSitelinks / topSitelinks;
        return ratio >= _options.RelativeSitelinkThreshold;
    }
}
