using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CashFlow.Entries.Domain;

namespace CashFlow.Entries.Infrastructure.Persistence;

/// <summary>
/// One link of a merchant's audit chain: what happened to the books (an entry registered or a reversal), who did it and
/// when. Each link carries the hash of the previous one, so changing or removing any link breaks every link after it.
/// Written in the same transaction as the entry; the table accepts only inserts (a database trigger refuses the rest).
/// </summary>
public sealed class AuditRecord
{
    /// <summary>Order of insertion across the whole table; within one merchant it follows the chain.</summary>
    public long Seq { get; init; }

    public Guid MerchantId { get; init; }

    public Guid EntryId { get; init; }

    public Guid EventId { get; init; }

    /// <summary>EntryRegistered or EntryReversed.</summary>
    public required string Action { get; init; }

    /// <summary>Who did it: the subject of the token.</summary>
    public required string Actor { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public EntryType EntryType { get; init; }

    public decimal Amount { get; init; }

    public DateOnly EntryDate { get; init; }

    public Guid? OriginalEntryId { get; init; }

    public required string PrevHash { get; init; }

    public required string Hash { get; set; }

    public static AuditRecord Chain(EntryEventContract e, string prevHash)
    {
        var occurredAt = AuditChain.Truncate(e.OccurredAt);
        var record = new AuditRecord
        {
            MerchantId = e.MerchantId,
            EntryId = e.EntryId,
            EventId = e.EventId,
            Action = e.EventType,
            Actor = e.CreatedBy,
            OccurredAt = occurredAt,
            EntryType = e.Type,
            Amount = e.Amount,
            EntryDate = e.EntryDate,
            OriginalEntryId = e.OriginalEntryId,
            PrevHash = prevHash,
            Hash = string.Empty,
        };

        record.Hash = AuditChain.HashOf(record);

        return record;
    }
}

public static class AuditChain
{
    /// <summary>The first link of every chain points here.</summary>
    public static readonly string Genesis = new('0', 64);

    /// <summary>PostgreSQL keeps microseconds; the hash must be computed on what will be read back.</summary>
    public static DateTimeOffset Truncate(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    /// <summary>
    /// SHA-256 over every field, in a fixed text form. The version prefix lets the form change later without ambiguity.
    /// </summary>
    public static string HashOf(AuditRecord r)
    {
        var canonical = string.Join(
            '|',
            "v1",
            r.MerchantId.ToString("D"),
            r.EntryId.ToString("D"),
            r.EventId.ToString("D"),
            r.Action,
            r.Actor,
            r.OccurredAt.UtcTicks.ToString(CultureInfo.InvariantCulture),
            ((short)r.EntryType).ToString(CultureInfo.InvariantCulture),
            r.Amount.ToString("F2", CultureInfo.InvariantCulture),
            r.EntryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            r.OriginalEntryId?.ToString("D") ?? "-",
            r.PrevHash);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
