namespace CashFlow.Entries.Domain;

public interface IDomainEvent
{
    Guid EventId { get; }

    DateTimeOffset OccurredAt { get; }
}

/// <summary>A new entry was registered by the merchant.</summary>
public sealed record EntryRegistered(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid MerchantId,
    Guid EntryId,
    EntryType Type,
    decimal Amount,
    DateOnly EntryDate,
    string CreatedBy) : IDomainEvent;

/// <summary>
/// A compensating entry was created to reverse <paramref name="OriginalEntryId"/>.
/// <paramref name="EntryId"/>, <paramref name="Type"/> and <paramref name="Amount"/> describe the compensating entry.
/// </summary>
public sealed record EntryReversed(
    Guid EventId,
    DateTimeOffset OccurredAt,
    Guid MerchantId,
    Guid EntryId,
    Guid OriginalEntryId,
    EntryType Type,
    decimal Amount,
    DateOnly EntryDate,
    string CreatedBy) : IDomainEvent;
