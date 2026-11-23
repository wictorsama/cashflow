using CashFlow.Entries.Application;
using CashFlow.Entries.Domain;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Entries.Infrastructure.Persistence;

public sealed class EntryRepository(EntriesDbContext db) : IEntryRepository
{
    public async Task AddAsync(Entry entry, CancellationToken cancellationToken) =>
        await db.Entries.AddAsync(entry, cancellationToken);

    public Task<Entry?> FindAsync(Guid merchantId, Guid entryId, CancellationToken cancellationToken) =>
        db.Entries.FirstOrDefaultAsync(e => e.MerchantId == merchantId && e.Id == entryId, cancellationToken);
}
