using CashFlow.Consolidation.Api;
using CashFlow.Consolidation.Infrastructure;
using CashFlow.Consolidation.Infrastructure.Persistence;
using CashFlow.Consolidation.Infrastructure.Reconciliation;
using CashFlow.Consolidation.Application;
using CashFlow.Observability;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddCashFlowObservability("cashflow-consolidation", [ConsolidationTelemetry.Name], [ConsolidationTelemetry.Name, CashFlow.Security.AuditTrail.MeterName]);

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
var rabbitMq = builder.Configuration["RabbitMq:Uri"]
    ?? throw new InvalidOperationException("RabbitMq:Uri is not configured.");
var redis = builder.Configuration["Redis:ConnectionString"]
    ?? throw new InvalidOperationException("Redis:ConnectionString is not configured.");

var reconciliation = builder.Configuration.GetSection("Reconciliation").Get<ReconciliationOptions>();
builder.Services.AddConsolidationInfrastructure(postgres, rabbitMq, redis, reconciliation);
builder.Services.AddCashFlowAuthentication(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

// Readiness depends only on this service's own database. Redis and RabbitMQ are deliberately NOT checked: the service
// degrades without them (cache fallback, outbox / queue buffering), so it must keep receiving traffic.
builder.Services.AddHealthChecks().AddDbContextCheck<ConsolidationDbContext>("postgres", tags: ["ready"]);

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<ConsolidationDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
app.MapBalanceEndpoints();

await app.RunAsync();

/// <summary>Exposed so the integration tests can start the whole API in memory.</summary>
public partial class Program;
