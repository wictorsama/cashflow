using CashFlow.Consolidation.Application;
using CashFlow.Consolidation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Consolidation.Infrastructure.Reconciliation;

public sealed class EfReconciliationStore(ConsolidationDbContext db) : IReconciliationStore
{
    private const short DebitType = 1;
    private const short CreditType = 2;

    // Plain shapes for the raw queries: EF maps result columns to properties by name.
    private sealed class TotalRow
    {
        public Guid MerchantId { get; set; }

        public DateOnly Date { get; set; }

        public decimal Credits { get; set; }

        public decimal Debits { get; set; }
    }

    private sealed class DriftRow
    {
        public Guid MerchantId { get; set; }

        public DateOnly Date { get; set; }

        public decimal ExpectedCredits { get; set; }

        public decimal ExpectedDebits { get; set; }

        public decimal ActualCredits { get; set; }

        public decimal ActualDebits { get; set; }
    }

    public async Task<IReadOnlyList<DailyTotal>> EventLogTotalsAsync(
        DateOnly fromDate, DateOnly toDate, DateTimeOffset occurredBefore, CancellationToken cancellationToken)
    {
        var rows = await db.Database.SqlQuery<TotalRow>($"""
            SELECT merchant_id AS "MerchantId",
                   entry_date AS "Date",
                   COALESCE(SUM(amount) FILTER (WHERE type = {CreditType}), 0) AS "Credits",
                   COALESCE(SUM(amount) FILTER (WHERE type = {DebitType}), 0) AS "Debits"
            FROM event_log
            WHERE entry_date >= {fromDate} AND entry_date <= {toDate} AND occurred_at <= {occurredBefore}
            GROUP BY merchant_id, entry_date
            """).ToListAsync(cancellationToken);

        return [.. rows.Select(r => new DailyTotal(r.MerchantId, r.Date, r.Credits, r.Debits))];
    }

    public async Task<IReadOnlyList<Divergence>> BalanceDriftAsync(
        DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        // One statement, so one consistent snapshot: the consumer updates the log and the balance in a single transaction.
        var rows = await db.Database.SqlQuery<DriftRow>($"""
            WITH log AS (
                SELECT merchant_id,
                       entry_date AS date,
                       COALESCE(SUM(amount) FILTER (WHERE type = {CreditType}), 0) AS credits,
                       COALESCE(SUM(amount) FILTER (WHERE type = {DebitType}), 0) AS debits
                FROM event_log
                WHERE entry_date >= {fromDate} AND entry_date <= {toDate}
                GROUP BY merchant_id, entry_date
            ), stored AS (
                SELECT merchant_id, date, credits, debits
                FROM daily_balances
                WHERE date >= {fromDate} AND date <= {toDate}
            )
            SELECT COALESCE(l.merchant_id, s.merchant_id) AS "MerchantId",
                   COALESCE(l.date, s.date) AS "Date",
                   COALESCE(l.credits, 0) AS "ExpectedCredits",
                   COALESCE(l.debits, 0) AS "ExpectedDebits",
                   COALESCE(s.credits, 0) AS "ActualCredits",
                   COALESCE(s.debits, 0) AS "ActualDebits"
            FROM log l
            FULL OUTER JOIN stored s ON l.merchant_id = s.merchant_id AND l.date = s.date
            WHERE COALESCE(l.credits, 0) <> COALESCE(s.credits, 0) OR COALESCE(l.debits, 0) <> COALESCE(s.debits, 0)
            """).ToListAsync(cancellationToken);

        return [.. rows.Select(r => new Divergence(
            DivergenceKind.EventsVsBalances, r.MerchantId, r.Date,
            r.ExpectedCredits, r.ExpectedDebits, r.ActualCredits, r.ActualDebits))];
    }
}
