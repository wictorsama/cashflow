using CashFlow.Entries.Application;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Entries.Infrastructure.Persistence;

public sealed class EntryQueries(EntriesDbContext db) : IEntryQueries
{
    public async Task<EntryDto?> GetAsync(Guid merchantId, Guid entryId, CancellationToken cancellationToken)
    {
        var entry = await db.Entries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.MerchantId == merchantId && e.Id == entryId, cancellationToken);

        return entry is null ? null : EntryDto.From(entry);
    }

    public async Task<PagedResult<EntryDto>> ListAsync(
        Guid merchantId, DateOnly fromDate, DateOnly toDate, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Entries.AsNoTracking()
            .Where(e => e.MerchantId == merchantId && e.EntryDate >= fromDate && e.EntryDate <= toDate);

        var total = await query.CountAsync(cancellationToken);

        var entries = await query
            .OrderByDescending(e => e.EntryDate)
            .ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<EntryDto>(entries.Select(EntryDto.From).ToList(), page, pageSize, total);
    }
}
