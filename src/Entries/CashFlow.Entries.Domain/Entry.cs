namespace CashFlow.Entries.Domain;

/// <summary>
/// A cash flow entry (debit or credit) of a merchant. Immutable after creation: corrections are made
/// by a reversal, which creates a new compensating entry. Only <see cref="ReversedBy"/> changes, once.
/// </summary>
public sealed class Entry
{
    public const int MaxDescriptionLength = 200;
    public const int MaxCreatedByLength = 100;
    public const decimal MaxAmount = 999_999_999_999.99m;

    private readonly List<IDomainEvent> _domainEvents = [];

    private Entry(
        Guid id,
        Guid merchantId,
        EntryType type,
        decimal amount,
        DateOnly entryDate,
        string description,
        string createdBy,
        DateTimeOffset registeredAt,
        Guid? reversalOf)
    {
        Id = id;
        MerchantId = merchantId;
        Type = type;
        Amount = amount;
        EntryDate = entryDate;
        Description = description;
        CreatedBy = createdBy;
        RegisteredAt = registeredAt;
        ReversalOf = reversalOf;
    }

    public Guid Id { get; }

    public Guid MerchantId { get; }

    public EntryType Type { get; }

    public decimal Amount { get; }

    /// <summary>Accounting day chosen by the merchant. The daily balance follows this date.</summary>
    public DateOnly EntryDate { get; }

    public string Description { get; }

    /// <summary>
    /// Who registered the entry: the subject of the access token (a person or an integration). For a reversal,
    /// who reversed. It never changes, so the audit trail does not depend on logs or traces that expire.
    /// </summary>
    public string CreatedBy { get; }

    /// <summary>Technical instant (UTC) when the entry was registered.</summary>
    public DateTimeOffset RegisteredAt { get; }

    /// <summary>Set when this entry is itself a reversal: the entry it reverses.</summary>
    public Guid? ReversalOf { get; }

    /// <summary>Set when this entry was reversed: the compensating entry.</summary>
    public Guid? ReversedBy { get; private set; }

    public bool IsReversal => ReversalOf is not null;

    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents;

    public void ClearDomainEvents() => _domainEvents.Clear();

    /// <param name="today">Current date for the merchant; entries cannot be dated in the future.</param>
    public static Entry Register(
        Guid merchantId,
        EntryType type,
        decimal amount,
        DateOnly entryDate,
        string? description,
        string createdBy,
        DateTimeOffset now,
        DateOnly today)
    {
        if (merchantId == Guid.Empty)
        {
            throw new DomainException("Merchant is required.");
        }

        if (!Enum.IsDefined(type))
        {
            throw new DomainException("Entry type must be Debit or Credit.");
        }

        ValidateAmount(amount);
        var actor = NormalizeCreatedBy(createdBy);

        if (entryDate > today)
        {
            throw new DomainException("Entry date cannot be in the future.");
        }

        var entry = new Entry(
            Guid.CreateVersion7(now),
            merchantId,
            type,
            amount,
            entryDate,
            NormalizeDescription(description),
            actor,
            now,
            reversalOf: null);

        entry._domainEvents.Add(new EntryRegistered(
            Guid.CreateVersion7(now), now, merchantId, entry.Id, type, amount, entryDate, actor));

        return entry;
    }

    /// <summary>
    /// Reverses this entry. Returns the compensating entry (opposite type, same amount, same entry date)
    /// and marks this entry as reversed. An entry can be reversed only once, and a reversal cannot be reversed.
    /// </summary>
    public Entry Reverse(string? reason, string createdBy, DateTimeOffset now)
    {
        var actor = NormalizeCreatedBy(createdBy);

        if (IsReversal)
        {
            throw new DomainException("A reversal entry cannot be reversed.");
        }

        if (ReversedBy is not null)
        {
            throw new EntryAlreadyReversedException(Id);
        }

        var description = string.IsNullOrWhiteSpace(reason) ? $"Reversal of entry {Id}" : reason;

        var reversal = new Entry(
            Guid.CreateVersion7(now),
            MerchantId,
            Type == EntryType.Debit ? EntryType.Credit : EntryType.Debit,
            Amount,
            EntryDate,
            NormalizeDescription(description),
            actor,
            now,
            reversalOf: Id);

        ReversedBy = reversal.Id;

        reversal._domainEvents.Add(new EntryReversed(
            Guid.CreateVersion7(now), now, MerchantId, reversal.Id, Id, reversal.Type, reversal.Amount, reversal.EntryDate, actor));

        return reversal;
    }

    private static void ValidateAmount(decimal amount)
    {
        if (amount <= 0)
        {
            throw new DomainException("Amount must be greater than zero.");
        }

        if (amount > MaxAmount)
        {
            throw new DomainException($"Amount cannot exceed {MaxAmount}.");
        }

        if (decimal.Round(amount, 2) != amount)
        {
            throw new DomainException("Amount cannot have more than two decimal places.");
        }
    }

    private static string NormalizeCreatedBy(string? createdBy)
    {
        var value = createdBy?.Trim() ?? string.Empty;

        if (value.Length == 0)
        {
            throw new DomainException("The author of the entry is required.");
        }

        if (value.Length > MaxCreatedByLength)
        {
            throw new DomainException($"The author of the entry cannot exceed {MaxCreatedByLength} characters.");
        }

        return value;
    }

    private static string NormalizeDescription(string? description)
    {
        var value = description?.Trim() ?? string.Empty;

        if (value.Length > MaxDescriptionLength)
        {
            throw new DomainException($"Description cannot exceed {MaxDescriptionLength} characters.");
        }

        return value;
    }
}
