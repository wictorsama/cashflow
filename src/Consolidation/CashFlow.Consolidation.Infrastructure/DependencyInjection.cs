using CashFlow.Consolidation.Application;
using CashFlow.Consolidation.Infrastructure.Caching;
using CashFlow.Consolidation.Infrastructure.Messaging;
using CashFlow.Consolidation.Infrastructure.Persistence;
using CashFlow.Consolidation.Infrastructure.Reconciliation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace CashFlow.Consolidation.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddConsolidationInfrastructure(
        this IServiceCollection services,
        string postgresConnectionString,
        string rabbitMqUri,
        string redisConnectionString,
        ReconciliationOptions? reconciliation = null)
    {
        services.AddDbContext<ConsolidationDbContext>(o => o.UseNpgsql(postgresConnectionString));

        // Short timeouts and no abort on first failure: a dead Redis must slow a read by milliseconds, not seconds.
        var redisOptions = ConfigurationOptions.Parse(redisConnectionString);
        redisOptions.AbortOnConnectFail = false;
        redisOptions.ConnectTimeout = 500;
        redisOptions.AsyncTimeout = 300;
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IBalanceCache, RedisBalanceCache>();
        services.AddScoped<IBalanceStore, EfBalanceStore>();
        services.AddScoped<EntryEventApplier>();
        services.AddScoped<GetBalanceHandler>();
        services.AddScoped<GetBalanceRangeHandler>();
        services.AddScoped<RebuildBalancesHandler>();

        var reconciliationOptions = reconciliation ?? new ReconciliationOptions();
        services.AddSingleton(reconciliationOptions);
        services.AddScoped<IReconciliationStore, EfReconciliationStore>();
        services.AddScoped<Reconciler>();
        if (reconciliationOptions.CanReadEntries)
        {
            services.AddHttpClient<IEntriesTotalsSource, EntriesTotalsClient>(http => http.Timeout = TimeSpan.FromSeconds(15));
        }
        else
        {
            services.AddSingleton<IEntriesTotalsSource, NotConfiguredEntriesTotals>();
        }

        if (reconciliationOptions is { Enabled: true, CanReadEntries: true })
        {
            services.AddHostedService<ReconciliationWorker>();
        }

        services.AddSingleton(new RabbitMqOptions { Uri = rabbitMqUri });
        services.AddHostedService<EntriesConsumer>();

        return services;
    }
}
