using CashFlow.Entries.Application;
using Microsoft.AspNetCore.Mvc;

namespace CashFlow.Entries.Api;

/// <summary>
/// Totals per merchant and day for the reconciliation job. It reads every merchant, so it needs its own scope, held
/// only by the job's client; the gateway does not route it, so it is reachable only from inside the cluster.
/// </summary>
public static class ReconciliationEndpoints
{
    public static void MapReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/reconciliation/daily-totals", GetDailyTotalsAsync).RequireAuthorization(Scopes.EntriesReconcile);

        // Checks the books against the tamper-evident audit chain. Same scope and same reason: it reads every merchant.
        app.MapGet("/v1/audit/verify", VerifyAuditTrailAsync).RequireAuthorization(Scopes.EntriesReconcile);
    }

    private static async Task<IResult> VerifyAuditTrailAsync(IAuditTrailVerifier verifier, CancellationToken cancellationToken) =>
        Results.Ok(await verifier.VerifyAsync(cancellationToken));

    private static async Task<IResult> GetDailyTotalsAsync(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] DateTimeOffset? registeredBefore,
        GetDailyTotalsHandler handler,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (from is null || to is null)
        {
            throw new InvalidRequestException("Query parameters 'from' and 'to' are required.");
        }

        var totals = await handler.HandleAsync(from.Value, to.Value, registeredBefore ?? clock.GetUtcNow(), cancellationToken);

        return Results.Ok(totals);
    }
}
