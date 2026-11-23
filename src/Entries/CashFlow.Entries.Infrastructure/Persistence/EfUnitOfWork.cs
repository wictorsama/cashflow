using CashFlow.Entries.Application;
using CashFlow.Entries.Domain;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Entries.Infrastructure.Persistence;

/// <summary>
/// Turns the domain events raised by the tracked entries into outbox rows and saves everything
/// in one database transaction: either the entry and its event are stored, or neither is.
/// </summary>
public sealed class EfUnitOfWork(EntriesDbContext db) : IUnitOfWork
{
    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        var entries = db.ChangeTracker.Entries<Entry>().Select(e => e.Entity).ToList();
        var contracts = entries.SelectMany(e => e.DomainEvents).Select(EntryEventContract.From).ToList();

        foreach (var contract in contracts)
        {
            await db.Outbox.AddAsync(contract.ToOutboxMessage(), cancellationToken);
        }

        // The audit chain is extended in the same transaction as the entry. Callers that already opened one (the
        // idempotency service) keep it; otherwise this opens its own so the lock below lasts until the commit.
        var ownTransaction = contracts.Count > 0 && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            await AppendToAuditChainAsync(contracts, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync();
            }
        }

        foreach (var entry in entries)
        {
            foreach (var domainEvent in entry.DomainEvents)
            {
                EntriesTelemetry.EventsRecorded.Add(1, new KeyValuePair<string, object?>("type", domainEvent.GetType().Name));
            }
        }

        foreach (var entry in entries)
        {
            entry.ClearDomainEvents();
        }
    }

    /// <summary>
    /// Adds one link per event to the merchant's chain. A transaction-level advisory lock per merchant makes two
    /// concurrent writes of the same merchant take their turn: each one reads the previous hash after the other committed.
    /// Different merchants do not wait for each other.
    /// </summary>
    private async Task AppendToAuditChainAsync(List<EntryEventContract> contracts, CancellationToken cancellationToken)
    {
        foreach (var merchant in contracts.GroupBy(c => c.MerchantId))
        {
            var key = merchant.Key.ToString("D");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);

            var previous = await db.AuditLog
                .Where(a => a.MerchantId == merchant.Key)
                .OrderByDescending(a => a.Seq)
                .Select(a => a.Hash)
                .FirstOrDefaultAsync(cancellationToken) ?? AuditChain.Genesis;

            foreach (var contract in merchant)
            {
                var link = AuditRecord.Chain(contract, previous);
                await db.AuditLog.AddAsync(link, cancellationToken);
                previous = link.Hash;
            }
        }
    }
}
