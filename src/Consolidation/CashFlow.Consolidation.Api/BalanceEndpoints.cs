using CashFlow.Consolidation.Application;
using CashFlow.Consolidation.Infrastructure.Reconciliation;
using CashFlow.Security;
using Microsoft.AspNetCore.Mvc;

namespace CashFlow.Consolidation.Api;

public sealed record RebuildRequest(Guid MerchantId, DateOnly From, DateOnly To);

public sealed record ReconcileRequest(DateOnly? From, DateOnly? To);

public static class BalanceEndpoints
{
    public static void MapBalanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/balances");

        group.MapGet("/{day}", GetDayAsync).RequireAuthorization(Scopes.BalancesReadOrAudit);
        group.MapGet("/", GetRangeAsync).RequireAuthorization(Scopes.BalancesReadOrAudit);
        group.MapPost("/rebuild", RebuildAsync).RequireAuthorization(Scopes.BalancesRebuild);
        group.MapPost("/reconcile", ReconcileAsync).RequireAuthorization(Scopes.BalancesRebuild);
    }

    private static async Task<IResult> GetDayAsync(
        HttpContext http, DateOnly day, [FromQuery] Guid? merchantId, GetBalanceHandler handler, CancellationToken cancellationToken)
    {
        var access = http.ResolveRead(merchantId, Scopes.BalancesAudit, "balance");

        return Results.Ok(await handler.HandleAsync(access.MerchantId, day, cancellationToken));
    }

    private static async Task<IResult> GetRangeAsync(
        HttpContext http,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] Guid? merchantId,
        GetBalanceRangeHandler handler,
        CancellationToken cancellationToken)
    {
        if (from is null || to is null)
        {
            throw new InvalidRequestException("Query parameters 'from' and 'to' are required.");
        }

        var access = http.ResolveRead(merchantId, Scopes.BalancesAudit, "balances");

        return Results.Ok(await handler.HandleAsync(access.MerchantId, from.Value, to.Value, cancellationToken));
    }

    // Administrative operation: requires the balances:rebuild scope.
    private static async Task<IResult> RebuildAsync(
        RebuildRequest request, RebuildBalancesHandler handler, CancellationToken cancellationToken)
    {
        var days = await handler.HandleAsync(new RebuildCommand(request.MerchantId, request.From, request.To), cancellationToken);

        return Results.Ok(new { rebuiltDays = days });
    }

    private const int MaxReportedDivergences = 200;

    // Administrative operation: compares the balances with what Entries registered, now instead of at the next scheduled run.
    private static async Task<IResult> ReconcileAsync(
        ReconcileRequest? request,
        Reconciler reconciler,
        ReconciliationOptions options,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddHours(-3)); // the merchant's day
        var to = request?.To ?? today;
        var from = request?.From ?? to.AddDays(-(Math.Max(1, options.WindowDays) - 1));

        var report = await reconciler.RunAsync(from, to, TimeSpan.FromSeconds(options.GraceSeconds), cancellationToken);

        return Results.Ok(new
        {
            consistent = report.IsConsistent,
            from = report.From,
            to = report.To,
            daysCompared = report.DaysCompared,
            cutoff = report.Cutoff,
            divergenceCount = report.Divergences.Count,
            divergences = report.Divergences.Take(MaxReportedDivergences).Select(d => new
            {
                kind = d.Kind.ToString(),
                merchantId = d.MerchantId,
                date = d.Date,
                expected = new { credits = d.ExpectedCredits, debits = d.ExpectedDebits },
                actual = new { credits = d.ActualCredits, debits = d.ActualDebits },
            }),
        });
    }
}
