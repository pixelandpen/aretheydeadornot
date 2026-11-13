using System.Globalization;
using System.Text;

namespace AreTheyDeadOrNot.Core.Services;

public static class NameNormalizer
{
    /// <summary>
    /// Normalizes a person's name for index-backed case- and accent-insensitive lookups:
    /// trims whitespace, collapses internal whitespace, removes diacritics, and lowercases.
    /// </summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var decomposed = input.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var prevWasSpace = false;

        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!prevWasSpace)
                {
                    sb.Append(' ');
                    prevWasSpace = true;
                }
            }
            else
            {
                sb.Append(char.ToLowerInvariant(ch));
                prevWasSpace = false;
            }
        }

        return sb.ToString().Trim().Normalize(NormalizationForm.FormC);
    }
}
