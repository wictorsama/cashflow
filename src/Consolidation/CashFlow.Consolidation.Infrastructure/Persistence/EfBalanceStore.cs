using CashFlow.Consolidation.Application;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Consolidation.Infrastructure.Persistence;

public sealed class EfBalanceStore(ConsolidationDbContext db) : IBalanceStore
{
    private const short DebitType = 1;
    private const short CreditType = 2;

    public async Task<bool> ApplyAsync(EntryEvent entryEvent, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        var type = entryEvent.IsCredit ? CreditType : DebitType;
        var credit = entryEvent.IsCredit ? entryEvent.Amount : 0m;
        var debit = entryEvent.IsCredit ? 0m : entryEvent.Amount;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Shared lock: events of any merchant are applied in parallel; only a rebuild of that merchant excludes them.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock_shared(hashtextextended({entryEvent.MerchantId.ToString()}, 0))", cancellationToken);

        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO event_log (event_id, event_type, merchant_id, entry_id, type, amount, entry_date,
                                   original_entry_id, occurred_at, received_at)
            VALUES ({entryEvent.EventId}, {entryEvent.EventType}, {entryEvent.MerchantId}, {entryEvent.EntryId}, {type},
                    {entryEvent.Amount}, {entryEvent.EntryDate}, {entryEvent.OriginalEntryId}, {entryEvent.OccurredAt},
                    {receivedAt})
            ON CONFLICT (event_id) DO NOTHING
            """, cancellationToken);

        if (inserted == 0)
        {
            return false;
        }

        // The sum is done by the database, atomically: concurrent events on the same day cannot lose an update.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO daily_balances (merchant_id, date, credits, debits, updated_at)
            VALUES ({entryEvent.MerchantId}, {entryEvent.EntryDate}, {credit}, {debit}, {receivedAt})
            ON CONFLICT (merchant_id, date) DO UPDATE SET
                credits = daily_balances.credits + EXCLUDED.credits,
                debits = daily_balances.debits + EXCLUDED.debits,
                updated_at = EXCLUDED.updated_at
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return true;
    }

    public async Task<DailyBalanceDto> GetDailyAsync(Guid merchantId, DateOnly day, CancellationToken cancellationToken)
    {
        var row = await db.DailyBalances.AsNoTracking()
            .FirstOrDefaultAsync(b => b.MerchantId == merchantId && b.Date == day, cancellationToken);

        var accumulated = await AccumulatedUpToAsync(merchantId, day, inclusive: true, cancellationToken);

        return ToDto(merchantId, day, row?.Credits ?? 0m, row?.Debits ?? 0m, accumulated);
    }

    public async Task<BalanceRangeDto> GetRangeAsync(
        Guid merchantId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        var opening = await AccumulatedUpToAsync(merchantId, fromDate, inclusive: false, cancellationToken);

        var rows = await db.DailyBalances.AsNoTracking()
            .Where(b => b.MerchantId == merchantId && b.Date >= fromDate && b.Date <= toDate)
            .OrderBy(b => b.Date)
            .ToListAsync(cancellationToken);

        var running = opening;
        var days = new List<DailyBalanceDto>(rows.Count);
        foreach (var row in rows)
        {
            running += row.Credits - row.Debits;
            days.Add(ToDto(merchantId, row.Date, row.Credits, row.Debits, running));
        }

        return new BalanceRangeDto(merchantId, fromDate, toDate, opening, running, days);
    }

    public async Task<int> RebuildAsync(
        Guid merchantId, DateOnly fromDate, DateOnly toDate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Exclusive lock: waits for events being applied and blocks new ones for this merchant until it commits.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({merchantId.ToString()}, 0))", cancellationToken);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM daily_balances WHERE merchant_id = {merchantId} AND date >= {fromDate} AND date <= {toDate}
            """, cancellationToken);

        var rebuilt = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO daily_balances (merchant_id, date, credits, debits, updated_at)
            SELECT merchant_id,
                   entry_date,
                   COALESCE(SUM(amount) FILTER (WHERE type = {CreditType}), 0),
                   COALESCE(SUM(amount) FILTER (WHERE type = {DebitType}), 0),
                   {now}
            FROM event_log
            WHERE merchant_id = {merchantId} AND entry_date >= {fromDate} AND entry_date <= {toDate}
            GROUP BY merchant_id, entry_date
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return rebuilt;
    }

    private async Task<decimal> AccumulatedUpToAsync(
        Guid merchantId, DateOnly date, bool inclusive, CancellationToken cancellationToken)
    {
        var query = db.DailyBalances.AsNoTracking().Where(b => b.MerchantId == merchantId);
        query = inclusive ? query.Where(b => b.Date <= date) : query.Where(b => b.Date < date);

        return await query.SumAsync(b => (decimal?)(b.Credits - b.Debits), cancellationToken) ?? 0m;
    }

    private static DailyBalanceDto ToDto(Guid merchantId, DateOnly date, decimal credits, decimal debits, decimal accumulated) =>
        new(merchantId, date, credits, debits, credits - debits, accumulated);
}
