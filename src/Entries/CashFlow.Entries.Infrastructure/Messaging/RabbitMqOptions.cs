namespace CashFlow.Entries.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string Exchange = "entries.events";

    // The queue the Consolidation service consumes, with its dead-letter pair. They belong to the consumer, but the
    // publisher declares them too (same names and arguments as Consolidation, idempotent): an exchange with no bound
    // queue drops a message silently, so an event published before that service ever started would be lost.
    // A test in the Consolidation integration tests fails if the two sides stop agreeing.
    public const string ConsumerQueue = "consolidation.entries";
    public const string DeadLetterExchange = "entries.events.dlx";
    public const string DeadLetterQueue = "consolidation.entries.dlq";
    public const string ConsumerBinding = "entry.*";

    /// <summary>AMQP URI, for example amqp://user:password@host:5672/.</summary>
    public required string Uri { get; init; }
}
