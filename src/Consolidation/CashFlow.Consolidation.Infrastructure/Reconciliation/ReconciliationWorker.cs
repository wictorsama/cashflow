using CashFlow.Consolidation.Application;
using CashFlow.Consolidation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CashFlow.Consolidation.Infrastructure.Reconciliation;

/// <summary>
/// Runs the reconciliation on a schedule. Every replica has this loop, but a database advisory lock lets only one of
/// them work at a time, so the Entries service is asked once per interval, not once per replica.
/// </summary>
public sealed partial class ReconciliationWorker(
    IServiceScopeFactory scopes, ReconciliationOptions options, TimeProvider clock, ILogger<ReconciliationWorker> logger)
    : BackgroundService
{
    // Arbitrary but fixed: every replica asks for the same lock.
    private const long LockKey = 7_351_001;
    private const int MaxLoggedDivergences = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the service settle after a start, then repeat.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.IntervalMinutes)));

        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                ConsolidationTelemetry.RecordReconciliationFailure();
                LogRunFailed(ex);
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ConsolidationDbContext>();

        // The lock belongs to the database session, so the connection is kept open for the whole run.
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var acquired = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"")
                .SingleAsync(cancellationToken);
            if (!acquired)
            {
                return; // another replica is running it
            }

            try
            {
                var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.AddHours(-3)); // the merchant's day
                var reconciler = scope.ServiceProvider.GetRequiredService<Reconciler>();
                var report = await reconciler.RunAsync(
                    today.AddDays(-(Math.Max(1, options.WindowDays) - 1)), today, TimeSpan.FromSeconds(options.GraceSeconds), cancellationToken);

                Log(report);
            }
            finally
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_unlock({LockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private void Log(ReconciliationReport report)
    {
        if (report.IsConsistent)
        {
            LogConsistent(report.DaysCompared, report.From, report.To);

            return;
        }

        LogDiverged(report.Divergences.Count, report.From, report.To);
        foreach (var d in report.Divergences.Take(MaxLoggedDivergences))
        {
            LogDivergence(d.Kind, d.MerchantId, d.Date, d.ExpectedCredits, d.ExpectedDebits, d.ActualCredits, d.ActualDebits);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconciliation consistent: {Days} day(s) from {From} to {To}.")]
    private partial void LogConsistent(int days, DateOnly from, DateOnly to);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciliation found {Count} divergence(s) from {From} to {To}.")]
    private partial void LogDiverged(int count, DateOnly from, DateOnly to);

    [LoggerMessage(Level = LogLevel.Error, Message = "Divergence {Kind}: merchant {MerchantId} on {Date}; expected credits {ExpectedCredits} / debits {ExpectedDebits}, found {ActualCredits} / {ActualDebits}.")]
    private partial void LogDivergence(
        DivergenceKind kind, Guid merchantId, DateOnly date, decimal expectedCredits, decimal expectedDebits, decimal actualCredits, decimal actualDebits);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reconciliation run failed.")]
    private partial void LogRunFailed(Exception exception);
}
