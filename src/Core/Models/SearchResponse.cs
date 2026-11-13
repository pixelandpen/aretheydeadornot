namespace AreTheyDeadOrNot.Core.Models;

public sealed record PersonDto(
    string Qid,
    string Name,
    bool IsDead,
    DateOnly? BirthDate,
    DateOnly? DeathDate,
    int? Age,
    int Sitelinks,
    IReadOnlyList<string> Occupations,
    string WikidataUrl)
{
    public static PersonDto FromRecord(PersonRecord record, DateOnly? referenceDate = null) =>
        new(
            record.Qid,
            record.Name,
            record.IsDead,
            record.BirthDate,
            record.DeathDate,
            record.ComputeAge(referenceDate),
            record.Sitelinks,
            record.Occupations,
            record.WikidataUrl);
}

public sealed record DidYouMeanCandidate(
    string Qid,
    string Name,
    bool IsDead,
    DateOnly? BirthDate,
    DateOnly? DeathDate,
    int Sitelinks,
    IReadOnlyList<string> Occupations);

public sealed record SearchResponse(
    string Query,
    bool Found,
    PersonDto? Person,
    IReadOnlyList<DidYouMeanCandidate> DidYouMean,
    long QueryTimeMs);
