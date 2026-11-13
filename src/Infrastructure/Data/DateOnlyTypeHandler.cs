using System.Data;
using Dapper;

namespace AreTheyDeadOrNot.Infrastructure.Data;

public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly?>
{
    public override void SetValue(IDbDataParameter parameter, DateOnly? value)
    {
        parameter.Value = value.HasValue ? value.Value : DBNull.Value;
        parameter.DbType = DbType.Date;
    }

    public override DateOnly? Parse(object value) => value switch
    {
        null or DBNull => null,
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        string s when DateOnly.TryParse(s, out var parsed) => parsed,
        _ => null
    };
}
