using System.Text.Json;

namespace CashFlow.Consolidation.Application;

/// <summary>The message can never be processed (malformed or unsupported). It must go to the dead-letter queue.</summary>
public sealed class InvalidEventException : Exception
{
    public InvalidEventException(string message)
        : base(message)
    {
    }

    public InvalidEventException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The event published by the Entries service (docs/03-arquitetura-alvo.md, section 5). The Consolidation
/// service owns its own copy of the contract so the two services stay independently deployable.
/// </summary>
public sealed record EntryEvent(
    Guid EventId,
    string EventType,
    int SchemaVersion,
    DateTimeOffset OccurredAt,
    Guid MerchantId,
    Guid EntryId,
    string Type,
    decimal Amount,
    DateOnly EntryDate,
    Guid? OriginalEntryId)
{
    public const int SupportedSchemaVersion = 1;
    public const string Debit = "Debit";
    public const string Credit = "Credit";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsCredit => Type == Credit;

    public static EntryEvent Parse(ReadOnlySpan<byte> json)
    {
        EntryEvent? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<EntryEvent>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidEventException("The event is not valid JSON for the entry event contract.", ex);
        }

        return parsed is null ? throw new InvalidEventException("The event is empty.") : parsed.Validated();
    }

    private EntryEvent Validated()
    {
        if (SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidEventException($"Unsupported schema version {SchemaVersion}.");
        }

        if (EventId == Guid.Empty || MerchantId == Guid.Empty || EntryId == Guid.Empty)
        {
            throw new InvalidEventException("The event is missing its ids.");
        }

        if (Type is not (Debit or Credit))
        {
            throw new InvalidEventException($"Unknown entry type '{Type}'.");
        }

        if (Amount <= 0)
        {
            throw new InvalidEventException("The amount must be greater than zero.");
        }

        return this;
    }
}
