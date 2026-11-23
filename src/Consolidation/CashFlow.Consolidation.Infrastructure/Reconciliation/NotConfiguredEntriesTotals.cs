using CashFlow.Consolidation.Application;

namespace CashFlow.Consolidation.Infrastructure.Reconciliation;

/// <summary>Stands in when the address of the Entries service or the job's credentials are not configured.</summary>
public sealed class NotConfiguredEntriesTotals : IEntriesTotalsSource
{
    public Task<IReadOnlyList<DailyTotal>> GetAsync(
        DateOnly fromDate, DateOnly toDate, DateTimeOffset registeredBefore, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "Reconciliation is not configured: set Reconciliation:EntriesUrl, TokenUrl and ClientId.");
}
