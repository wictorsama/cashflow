using CashFlow.Entries.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CashFlow.Entries.Infrastructure.Persistence;

public sealed class AuditTrailOptions
{
    public bool Enabled { get; init; } = true;

    public int IntervalMinutes { get; init; } = 10;
}

/// <summary>
/// Verifies the audit chain on a schedule. Every replica has the loop, but an advisory lock lets one work at a time.
/// A problem raises a metric (and an alert) and a log line; nothing is repaired automatically.
/// </summary>
public sealed partial class AuditTrailWorker(
    IServiceScopeFactory scopes, AuditTrailOptions options, TimeProvider clock, ILogger<AuditTrailWorker> logger) : BackgroundService
{
    private const long LockKey = 7_351_002;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
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
                EntriesTelemetry.AuditRuns.Add(1, new KeyValuePair<string, object?>("result", "failed"));
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
        var db = scope.ServiceProvider.GetRequiredService<EntriesDbContext>();

        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var acquired = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({LockKey}) AS \"Value\"").SingleAsync(cancellationToken);
            if (!acquired)
            {
                return;
            }

            try
            {
                var report = await scope.ServiceProvider.GetRequiredService<IAuditTrailVerifier>().VerifyAsync(cancellationToken);
                EntriesTelemetry.RecordAuditVerification(report.IssueCount, clock.GetUtcNow());
                EntriesTelemetry.AuditRuns.Add(1, new KeyValuePair<string, object?>("result", report.IsIntact ? "intact" : "tampered"));

                if (report.IsIntact)
                {
                    LogIntact(report.Merchants, report.Links);
                }
                else
                {
                    foreach (var issue in report.Issues.Take(20))
                    {
                        LogIssue(issue.Kind.ToString(), issue.MerchantId, issue.EntryId, issue.Detail);
                    }

                    LogTampered(report.IssueCount);
                }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit chain intact: {Merchants} merchant(s), {Links} link(s).")]
    private partial void LogIntact(int merchants, long links);

    [LoggerMessage(Level = LogLevel.Critical, Message = "AUDIT CHAIN BROKEN: {Count} problem(s) found. The books were changed outside the service.")]
    private partial void LogTampered(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit problem {Kind} merchant {MerchantId} entry {EntryId}: {Detail}")]
    private partial void LogIssue(string kind, Guid merchantId, Guid? entryId, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "The audit chain verification failed to run.")]
    private partial void LogRunFailed(Exception exception);
}
