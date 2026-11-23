using CashFlow.Entries.Application;
using CashFlow.Entries.Domain;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Entries.Infrastructure.Persistence;

public sealed class ReconciliationQueries(EntriesDbContext db) : IReconciliationQueries
{
    public async Task<IReadOnlyList<DailyTotal>> GetDailyTotalsAsync(
        DateOnly fromDate, DateOnly toDate, DateTimeOffset registeredBefore, CancellationToken cancellationToken)
    {
        // Entries registered after the cutoff may not have reached the Consolidation yet; they are left out on purpose.
        return await db.Entries.AsNoTracking()
            .Where(e => e.EntryDate >= fromDate && e.EntryDate <= toDate && e.RegisteredAt <= registeredBefore)
            .GroupBy(e => new { e.MerchantId, e.EntryDate })
            .Select(g => new DailyTotal(
                g.Key.MerchantId,
                g.Key.EntryDate,
                g.Where(e => e.Type == EntryType.Credit).Sum(e => (decimal?)e.Amount) ?? 0m,
                g.Where(e => e.Type == EntryType.Debit).Sum(e => (decimal?)e.Amount) ?? 0m))
            .ToListAsync(cancellationToken);
    }
}
