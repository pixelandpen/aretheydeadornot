using AreTheyDeadOrNot.Core.Models;

namespace AreTheyDeadOrNot.Core.UnitTests;

public sealed class PersonRecordTests
{
    [Fact]
    public void IsDead_ReflectsPresenceOfDeathDate()
    {
        var alive = new PersonRecord { Qid = "Q1", Name = "Alive Person", BirthDate = new DateOnly(1980, 1, 1) };
        var dead = new PersonRecord { Qid = "Q2", Name = "Dead Person", BirthDate = new DateOnly(1958, 8, 29), DeathDate = new DateOnly(2009, 6, 25) };

        Assert.False(alive.IsDead);
        Assert.True(dead.IsDead);
        Assert.Equal(50, dead.ComputeAge());
    }

    [Fact]
    public void ComputeAge_AccountsForBirthdayNotYetReached()
    {
        var person = new PersonRecord { Qid = "Q1", Name = "Test", BirthDate = new DateOnly(2000, 12, 25) };
        var age = person.ComputeAge(new DateOnly(2026, 11, 13));

        Assert.Equal(25, age);
    }
}
