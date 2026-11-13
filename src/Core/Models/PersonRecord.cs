namespace AreTheyDeadOrNot.Core.Models;

public sealed record PersonRecord
{
    public required string Qid { get; init; }
    public required string Name { get; init; }
    public DateOnly? BirthDate { get; init; }
    public DateOnly? DeathDate { get; init; }
    public int Sitelinks { get; init; }
    public IReadOnlyList<string> Occupations { get; init; } = [];

    public bool IsDead => DeathDate.HasValue;

    public int? ComputeAge(DateOnly? referenceDate = null)
    {
        if (!BirthDate.HasValue)
        {
            return null;
        }

        var end = DeathDate ?? referenceDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var age = end.Year - BirthDate.Value.Year;
        if (end < BirthDate.Value.AddYears(age))
        {
            age--;
        }

        return age >= 0 ? age : null;
    }

    public string WikidataUrl => $"https://www.wikidata.org/wiki/{Qid}";
}
