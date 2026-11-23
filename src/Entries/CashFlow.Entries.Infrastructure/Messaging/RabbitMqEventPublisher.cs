using System.Diagnostics;
using System.Text;
using CashFlow.Entries.Infrastructure.Persistence;
using RabbitMQ.Client;

namespace CashFlow.Entries.Infrastructure.Messaging;

/// <summary>
/// Publishes to a durable topic exchange with persistent messages and publisher confirms.
/// The connection is created on first use and rebuilt after a failure.
/// </summary>
public sealed class RabbitMqEventPublisher(RabbitMqOptions options) : IEventPublisher, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var channel = await GetChannelAsync(cancellationToken);

            // The producer span continues the trace of the request that created the event (stored with the outbox row),
            // and its context travels in the message so the consumer can continue it too.
            ActivityContext.TryParse(message.TraceParent, null, out var parent);
            using var activity = EntriesTelemetry.Source.StartActivity("entries.events publish", ActivityKind.Producer, parent);
            activity?.SetTag("messaging.system", "rabbitmq");
            activity?.SetTag("messaging.destination.name", RabbitMqOptions.Exchange);
            activity?.SetTag("messaging.operation.type", "publish");
            activity?.SetTag("messaging.message.id", message.Id.ToString());

            var properties = new BasicProperties
            {
                MessageId = message.Id.ToString(),
                Type = message.Type,
                ContentType = "application/json",
                DeliveryMode = DeliveryModes.Persistent,
                Timestamp = new AmqpTimestamp(message.OccurredAt.ToUnixTimeSeconds()),
                Headers = activity?.Id is { } traceParent ? new Dictionary<string, object?> { ["traceparent"] = traceParent } : null,
            };

            // With confirmation tracking enabled this completes only after the broker acknowledges the message.
            await channel.BasicPublishAsync(
                RabbitMqOptions.Exchange,
                RoutingKeyFor(message.Type),
                mandatory: false,
                properties,
                Encoding.UTF8.GetBytes(message.Payload),
                cancellationToken);
        }
        catch
        {
            await ResetAsync();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>EntryRegistered becomes "entry.registered".</summary>
    public static string RoutingKeyFor(string eventType) =>
        eventType.StartsWith("Entry", StringComparison.Ordinal)
            ? "entry." + eventType["Entry".Length..].ToLowerInvariant()
            : eventType.ToLowerInvariant();

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await ResetAsync();

        var factory = new ConnectionFactory { Uri = new Uri(options.Uri), AutomaticRecoveryEnabled = true };
        _connection = await factory.CreateConnectionAsync(cancellationToken);
        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);

        await _channel.ExchangeDeclareAsync(
            RabbitMqOptions.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);

        await DeclareConsumerQueueAsync(_channel, cancellationToken);

        return _channel;
    }

    /// <summary>Makes sure events published now wait in a queue even if the consumer has never started.</summary>
    public static async Task DeclareConsumerQueueAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            RabbitMqOptions.DeadLetterExchange, ExchangeType.Fanout, durable: true, cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            RabbitMqOptions.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            RabbitMqOptions.DeadLetterQueue, RabbitMqOptions.DeadLetterExchange, string.Empty, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            RabbitMqOptions.ConsumerQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum", // must match the consumer declaration exactly
                ["x-dead-letter-exchange"] = RabbitMqOptions.DeadLetterExchange,
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            RabbitMqOptions.ConsumerQueue, RabbitMqOptions.Exchange, RabbitMqOptions.ConsumerBinding, cancellationToken: cancellationToken);
    }

    private async Task ResetAsync()
    {
        try
        {
            if (_channel is not null)
            {
                await _channel.DisposeAsync();
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync();
            }
        }
        catch (Exception)
        {
            // The old connection is already broken; a fresh one is created on the next publish.
        }

        _channel = null;
        _connection = null;
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync();
        _gate.Dispose();
    }
}
