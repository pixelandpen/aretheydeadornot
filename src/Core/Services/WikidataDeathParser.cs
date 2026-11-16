using System.Text.Json;

namespace AreTheyDeadOrNot.Core.Services;

public static class WikidataDeathParser
{
    /// <summary>
    /// Extracts P570 (date of death) values from a Wikidata wbgetentities JSON response.
    /// Expects precision >= 11 (day precision) and ISO-like time strings such as "+2026-11-14T00:00:00Z".
    /// </summary>
    public static Dictionary<string, DateOnly> ParseDeathDates(string json)
    {
        var results = new Dictionary<string, DateOnly>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return results;
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("entities", out var entities) ||
            entities.ValueKind != JsonValueKind.Object)
        {
            return results;
        }

        foreach (var entity in entities.EnumerateObject())
        {
            if (!entity.Value.TryGetProperty("claims", out var claims) ||
                !claims.TryGetProperty("P570", out var p570) ||
                p570.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var claim in p570.EnumerateArray())
            {
                if (!claim.TryGetProperty("mainsnak", out var mainsnak) ||
                    !mainsnak.TryGetProperty("datavalue", out var datavalue) ||
                    !datavalue.TryGetProperty("value", out var value))
                {
                    continue;
                }

                if (value.TryGetProperty("precision", out var precisionProp) &&
                    precisionProp.TryGetInt32(out var precision) &&
                    precision < 11)
                {
                    continue;
                }

                if (!value.TryGetProperty("time", out var timeProp))
                {
                    continue;
                }

                var raw = timeProp.GetString()?.TrimStart('+');
                if (raw is not null && raw.Length >= 10 &&
                    DateOnly.TryParseExact(raw[..10], "yyyy-MM-dd", out var deathDate))
                {
                    results[entity.Name] = deathDate;
                    break;
                }
            }
        }

        return results;
    }
}
