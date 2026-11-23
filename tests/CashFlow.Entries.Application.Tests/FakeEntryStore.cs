using CashFlow.Entries.Domain;

namespace CashFlow.Entries.Application.Tests;

internal sealed class FakeEntryStore : IEntryRepository, IUnitOfWork
{
    public List<Entry> Entries { get; } = [];

    public int SaveCount { get; private set; }

    public Task AddAsync(Entry entry, CancellationToken cancellationToken)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<Entry?> FindAsync(Guid merchantId, Guid entryId, CancellationToken cancellationToken) =>
        Task.FromResult(Entries.FirstOrDefault(e => e.MerchantId == merchantId && e.Id == entryId));

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}
