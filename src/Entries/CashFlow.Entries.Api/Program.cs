using System.Text.Json.Serialization;
using CashFlow.Entries.Api;
using CashFlow.Entries.Infrastructure.Persistence;
using CashFlow.Entries.Infrastructure;
using CashFlow.Observability;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddCashFlowObservability("cashflow-entries", [EntriesTelemetry.Name], [EntriesTelemetry.Name, CashFlow.Security.AuditTrail.MeterName]);

var postgres = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
var rabbitMq = builder.Configuration["RabbitMq:Uri"]
    ?? throw new InvalidOperationException("RabbitMq:Uri is not configured.");

builder.Services.AddEntriesInfrastructure(
    postgres,
    rabbitMq,
    new AuditTrailOptions
    {
        Enabled = builder.Configuration.GetValue("AuditTrail:Enabled", true),
        IntervalMinutes = builder.Configuration.GetValue("AuditTrail:IntervalMinutes", 10),
    });

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddCashFlowAuthentication(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

// Readiness depends only on this service's own database. Redis and RabbitMQ are deliberately NOT checked: the service
// degrades without them (cache fallback, outbox / queue buffering), so it must keep receiving traffic.
builder.Services.AddHealthChecks().AddDbContextCheck<EntriesDbContext>("postgres", tags: ["ready"]);

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<EntriesDbContext>().Database.MigrateAsync();
}

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") }).AllowAnonymous();
app.MapEntryEndpoints();
app.MapReconciliationEndpoints();

await app.RunAsync();

/// <summary>Exposed so the integration tests can start the whole API in memory.</summary>
public partial class Program;
