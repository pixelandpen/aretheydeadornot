using AreTheyDeadOrNot.Core.Models;

namespace AreTheyDeadOrNot.Core.Repositories;

public interface IPersonRepository
{
    Task<IReadOnlyList<PersonRecord>> SearchByNameAsync(
        string normalizedName,
        int limit = 5,
        CancellationToken cancellationToken = default);

    Task<PersonRecord?> GetByQidAsync(
        string qid,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PersonRecord>> GetRecentDeathsAsync(
        int limit = 12,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PersonRecord>> GetInMemoryAsync(
        IReadOnlyList<short> monthDayKeys,
        int limit = 12,
        CancellationToken cancellationToken = default);
}
