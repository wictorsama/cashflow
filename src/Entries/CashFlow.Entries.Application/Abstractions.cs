using CashFlow.Entries.Domain;

namespace CashFlow.Entries.Application;

public interface IEntryRepository
{
    Task AddAsync(Entry entry, CancellationToken cancellationToken);

    Task<Entry?> FindAsync(Guid merchantId, Guid entryId, CancellationToken cancellationToken);
}

/// <summary>
/// Commits the entry changes and the pending domain events (Outbox) in a single transaction.
/// </summary>
public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
