using AreTheyDeadOrNot.Core.Models;
using AreTheyDeadOrNot.Core.Services;

namespace AreTheyDeadOrNot.Core.UnitTests;

public sealed class DisambiguationServiceTests
{
    private readonly DisambiguationService _sut = new();

    [Fact]
    public void SelectBestMatch_ReturnsNull_WhenNoCandidates()
    {
        var (primary, didYouMean) = _sut.SelectBestMatch([]);

        Assert.Null(primary);
        Assert.Empty(didYouMean);
    }

    [Fact]
    public void SelectBestMatch_ReturnsTopBySitelinks_AndExcludesDistantCandidates()
    {
        var candidates = new[]
        {
            new PersonRecord { Qid = "Q2", Name = "Michael Jackson", Sitelinks = 25, Occupations = ["journalist"] },
            new PersonRecord { Qid = "Q1", Name = "Michael Jackson", Sitelinks = 324, DeathDate = new DateOnly(2009, 6, 25) }
        };

        var (primary, didYouMean) = _sut.SelectBestMatch(candidates);

        Assert.NotNull(primary);
        Assert.Equal("Q1", primary.Qid);
        Assert.Empty(didYouMean);
    }

    [Fact]
    public void SelectBestMatch_IncludesCloseSecondAndThird_InDidYouMean()
    {
        var candidates = new[]
        {
            new PersonRecord { Qid = "Q1", Name = "John Williams", Sitelinks = 120 },
            new PersonRecord { Qid = "Q2", Name = "John Williams", Sitelinks = 60 },
            new PersonRecord { Qid = "Q3", Name = "John Williams", Sitelinks = 30 },
            new PersonRecord { Qid = "Q4", Name = "John Williams", Sitelinks = 25 }
        };

        var (primary, didYouMean) = _sut.SelectBestMatch(candidates);

        Assert.NotNull(primary);
        Assert.Equal("Q1", primary.Qid);
        Assert.Equal(2, didYouMean.Count);
        Assert.Equal(["Q2", "Q3"], didYouMean.Select(d => d.Qid).ToArray());
    }
}
