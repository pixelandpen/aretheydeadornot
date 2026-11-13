using AreTheyDeadOrNot.Core.Repositories;
using AreTheyDeadOrNot.Core.Services;
using AreTheyDeadOrNot.Infrastructure.Data;
using AreTheyDeadOrNot.Infrastructure.Repositories;
using Dapper;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AreTheyDeadOrNot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        SqlMapper.AddTypeHandler(new DateOnlyTypeHandler());

        var readConnectionString = configuration.GetConnectionString("PostgresReadReplica")
            ?? configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException(
                "Connection string 'PostgresReadReplica' (or 'Postgres') must be configured.");

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(readConnectionString);
        var dataSource = dataSourceBuilder.Build();

        services.AddSingleton(new ReadReplicaDataSource(dataSource));
        services.AddScoped<IPersonRepository, PostgresPersonRepository>();
        services.AddSingleton(new DisambiguationService());

        var redisConnection = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            services.AddStackExchangeRedisCache(options =>
            {
                options.Configuration = redisConnection;
                options.InstanceName = "atdon:";
            });
        }

        services.AddHybridCache(options =>
        {
            options.MaximumPayloadBytes = 1024 * 1024;
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(30),
                LocalCacheExpiration = TimeSpan.FromMinutes(5)
            };
        });

        return services;
    }
}
