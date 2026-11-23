using CashFlow.Entries.Domain;

namespace CashFlow.Entries.Application;

public sealed record EntryDto(
    Guid Id,
    Guid MerchantId,
    EntryType Type,
    decimal Amount,
    DateOnly EntryDate,
    string Description,
    string CreatedBy,
    DateTimeOffset RegisteredAt,
    Guid? ReversalOf,
    Guid? ReversedBy)
{
    public static EntryDto From(Entry entry) => new(
        entry.Id,
        entry.MerchantId,
        entry.Type,
        entry.Amount,
        entry.EntryDate,
        entry.Description,
        entry.CreatedBy,
        entry.RegisteredAt,
        entry.ReversalOf,
        entry.ReversedBy);
}
