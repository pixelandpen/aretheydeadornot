using AreTheyDeadOrNot.Core.Services;

namespace AreTheyDeadOrNot.Core.UnitTests;

public sealed class WikidataDeathParserTests
{
    [Fact]
    public void ParseDeathDates_ExtractsDayPrecisionP570()
    {
        const string json = """
            {
              "entities": {
                "Q7611527": {
                  "claims": {
                    "P570": [
                      {
                        "mainsnak": {
                          "datavalue": {
                            "value": {
                              "time": "+2026-11-14T00:00:00Z",
                              "precision": 11
                            }
                          }
                        }
                      }
                    ]
                  }
                },
                "Q999999": {
                  "claims": {}
                }
              }
            }
            """;

        var result = WikidataDeathParser.ParseDeathDates(json);

        Assert.Single(result);
        Assert.Equal(new DateOnly(2026, 11, 14), result["Q7611527"]);
    }

    [Fact]
    public void ParseDeathDates_IgnoresLowPrecisionDates()
    {
        const string json = """
            {
              "entities": {
                "Q123": {
                  "claims": {
                    "P570": [
                      {
                        "mainsnak": {
                          "datavalue": {
                            "value": {
                              "time": "+2026-00-00T00:00:00Z",
                              "precision": 9
                            }
                          }
                        }
                      }
                    ]
                  }
                }
              }
            }
            """;

        var result = WikidataDeathParser.ParseDeathDates(json);
        Assert.Empty(result);
    }
}
