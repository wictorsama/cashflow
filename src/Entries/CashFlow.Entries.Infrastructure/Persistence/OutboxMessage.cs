namespace CashFlow.Entries.Infrastructure.Persistence;

/// <summary>An integration event waiting to be published, stored in the same transaction as the entry.</summary>
public sealed class OutboxMessage
{
    /// <summary>Same value as the event id, so consumers can deduplicate on it.</summary>
    public Guid Id { get; init; }

    /// <summary>For example "EntryRegistered"; also used to build the routing key.</summary>
    public required string Type { get; init; }

    /// <summary>The event contract serialized as JSON.</summary>
    public required string Payload { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>W3C trace context of the request that produced the event, so the trace continues across the broker.</summary>
    public string? TraceParent { get; init; }

    public DateTimeOffset? PublishedAt { get; set; }
}
