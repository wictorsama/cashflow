using System.Text.Json;
using System.Text.Json.Serialization;
using CashFlow.Entries.Domain;

namespace CashFlow.Entries.Infrastructure.Persistence;

/// <summary>
/// The event published to consumers (docs/03-arquitetura-alvo.md). Changing it is a contract change:
/// bump <see cref="CurrentSchemaVersion"/> and keep consumers compatible.
/// </summary>
public sealed record EntryEventContract(
    Guid EventId,
    string EventType,
    int SchemaVersion,
    DateTimeOffset OccurredAt,
    Guid MerchantId,
    Guid EntryId,
    EntryType Type,
    decimal Amount,
    DateOnly EntryDate,
    Guid? OriginalEntryId,
    string CreatedBy)
{
    public const int CurrentSchemaVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static EntryEventContract From(IDomainEvent domainEvent) => domainEvent switch
    {
        EntryRegistered e => new(
            e.EventId, nameof(EntryRegistered), CurrentSchemaVersion, e.OccurredAt,
            e.MerchantId, e.EntryId, e.Type, e.Amount, e.EntryDate, OriginalEntryId: null, e.CreatedBy),
        EntryReversed e => new(
            e.EventId, nameof(EntryReversed), CurrentSchemaVersion, e.OccurredAt,
            e.MerchantId, e.EntryId, e.Type, e.Amount, e.EntryDate, e.OriginalEntryId, e.CreatedBy),
        _ => throw new NotSupportedException($"Unsupported domain event {domainEvent.GetType().Name}."),
    };

    public OutboxMessage ToOutboxMessage() => new()
    {
        Id = EventId,
        Type = EventType,
        Payload = JsonSerializer.Serialize(this, JsonOptions),
        OccurredAt = OccurredAt,
        TraceParent = System.Diagnostics.Activity.Current?.Id,
    };

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
