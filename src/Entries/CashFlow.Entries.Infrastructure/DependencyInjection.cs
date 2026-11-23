using CashFlow.Entries.Application;
using CashFlow.Entries.Infrastructure.Messaging;
using CashFlow.Entries.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CashFlow.Entries.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddEntriesInfrastructure(
        this IServiceCollection services, string postgresConnectionString, string rabbitMqUri, AuditTrailOptions? audit = null)
    {
        // Singleton factory for the relay (no request scope); EF also registers a scoped context for HTTP requests.
        services.AddDbContextFactory<EntriesDbContext>(o => o.UseNpgsql(postgresConnectionString));

        services.AddScoped<IEntryRepository, EntryRepository>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IEntryQueries, EntryQueries>();
        services.AddScoped<IdempotencyService>();
        services.AddScoped<RegisterEntryHandler>();
        services.AddScoped<ReverseEntryHandler>();
        services.AddScoped<ListEntriesHandler>();
        services.AddScoped<IReconciliationQueries, ReconciliationQueries>();
        services.AddScoped<GetDailyTotalsHandler>();
        services.AddScoped<IAuditTrailVerifier, AuditTrailVerifier>();
        services.AddSingleton(audit ?? new AuditTrailOptions());
        services.AddHostedService<AuditTrailWorker>();

        services.AddSingleton(new RabbitMqOptions { Uri = rabbitMqUri });
        services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<OutboxProcessor>();
        services.AddHostedService<OutboxRelay>();

        return services;
    }
}
