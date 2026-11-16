using AreTheyDeadOrNot.Core.Repositories;
using AreTheyDeadOrNot.Core.Services;
using AreTheyDeadOrNot.Infrastructure.Data;
using AreTheyDeadOrNot.Infrastructure.Repositories;
using AreTheyDeadOrNot.Infrastructure.Services;
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

        var writeConnectionString = configuration.GetConnectionString("PostgresPrimary")
            ?? readConnectionString;

        var readDataSource = new NpgsqlDataSourceBuilder(readConnectionString).Build();
        var writeDataSource = writeConnectionString == readConnectionString
            ? readDataSource
            : new NpgsqlDataSourceBuilder(writeConnectionString).Build();

        services.AddSingleton(new ReadReplicaDataSource(readDataSource));
        services.AddSingleton(new PrimaryWriteDataSource(writeDataSource));
        services.AddScoped<IPersonRepository, PostgresPersonRepository>();
        services.AddScoped<IDeathSyncRepository, PostgresDeathSyncRepository>();
        services.AddSingleton(new DisambiguationService());

        services.AddHttpClient<WikidataDeathClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "AreTheyDeadOrNot-DeathSync/1.0 (https://github.com/pixelandpen/aretheydeadornot)");
        });

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
