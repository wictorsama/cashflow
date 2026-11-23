using CashFlow.Entries.Application;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Entries.Infrastructure.Persistence;

/// <summary>
/// Reads the whole chain and the entries and reports every disagreement. It only reads: finding a problem here means
/// someone or something changed the data outside the service, and what to do about it is a decision for people.
/// Entries registered before the chain existed are not expected to be in it.
/// </summary>
public sealed class AuditTrailVerifier(EntriesDbContext db, TimeProvider clock) : IAuditTrailVerifier
{
    /// <summary>A report lists at most this many problems; the count is always exact.</summary>
    public const int MaxListedIssues = 100;

    public async Task<AuditReport> VerifyAsync(CancellationToken cancellationToken)
    {
        var startedAt = await db.Database.SqlQuery<DateTimeOffset>($"SELECT value::timestamptz AS \"Value\" FROM audit_meta WHERE key = 'started_at'")
            .SingleAsync(cancellationToken);

        var issues = new List<AuditIssue>();
        var count = 0;
        void Add(AuditIssueKind kind, Guid merchant, Guid? entry, long? seq, string detail)
        {
            count++;
            if (issues.Count < MaxListedIssues)
            {
                issues.Add(new AuditIssue(kind, merchant, entry, seq, detail));
            }
        }

        // 1. The chain itself: every link points at the previous one and still hashes to what it says.
        var merchants = 0;
        long links = 0;
        Guid? current = null;
        var expectedPrevious = AuditChain.Genesis;
        await foreach (var link in db.AuditLog.AsNoTracking().OrderBy(a => a.MerchantId).ThenBy(a => a.Seq).AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            if (current != link.MerchantId)
            {
                current = link.MerchantId;
                expectedPrevious = AuditChain.Genesis;
                merchants++;
            }

            links++;
            if (link.PrevHash != expectedPrevious)
            {
                Add(AuditIssueKind.ChainBroken, link.MerchantId, link.EntryId, link.Seq, "The link does not follow the previous one: a link was removed, inserted or reordered.");
            }

            if (AuditChain.HashOf(link) != link.Hash)
            {
                Add(AuditIssueKind.ChainBroken, link.MerchantId, link.EntryId, link.Seq, "The content of the link no longer matches its hash: it was changed.");
            }

            expectedPrevious = link.Hash;
        }

        // 2. Entries against the chain: nothing added, nothing edited (entries from before the chain are left out).
        var entries = db.Entries.AsNoTracking()
            .Where(e => e.RegisteredAt >= startedAt)
            .GroupJoin(db.AuditLog.AsNoTracking(), e => e.Id, a => a.EntryId, (e, links) => new { e, links })
            .SelectMany(x => x.links.DefaultIfEmpty(), (x, a) => new { x.e.Id, x.e.MerchantId, x.e.Type, x.e.Amount, x.e.EntryDate, Link = a })
            .AsAsyncEnumerable();
        await foreach (var row in entries.WithCancellation(cancellationToken))
        {
            if (row.Link is null)
            {
                Add(AuditIssueKind.EntryWithoutAudit, row.MerchantId, row.Id, null, "The entry is not in the audit chain.");
            }
            else if (row.Link.MerchantId != row.MerchantId || row.Link.EntryType != row.Type || row.Link.Amount != row.Amount || row.Link.EntryDate != row.EntryDate)
            {
                Add(AuditIssueKind.EntryAltered, row.MerchantId, row.Id, row.Link.Seq, "The entry no longer matches what the chain recorded.");
            }
        }

        // 3. The chain against the entries: every recorded entry still exists.
        var orphans = db.AuditLog.AsNoTracking()
            .Where(a => a.OccurredAt >= startedAt && !db.Entries.Any(e => e.Id == a.EntryId))
            .Select(a => new { a.MerchantId, a.EntryId, a.Seq })
            .AsAsyncEnumerable();
        await foreach (var orphan in orphans.WithCancellation(cancellationToken))
        {
            Add(AuditIssueKind.AuditWithoutEntry, orphan.MerchantId, orphan.EntryId, orphan.Seq, "The chain recorded this entry but it no longer exists.");
        }

        return new AuditReport(count == 0, merchants, links, startedAt, clock.GetUtcNow(), count, issues);
    }
}
