using AreTheyDeadOrNot.Core.Services;

namespace AreTheyDeadOrNot.Core.UnitTests;

public sealed class NameNormalizerTests
{
    [Theory]
    [InlineData("Michael Jackson", "michael jackson")]
    [InlineData("  Michael   Jackson  ", "michael jackson")]
    [InlineData("Joséphine Baker", "josephine baker")]
    [InlineData("Pelé", "pele")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Normalize_ProducesExpectedCanonicalForm(string? input, string expected)
    {
        var actual = NameNormalizer.Normalize(input);
        Assert.Equal(expected, actual);
    }
}
