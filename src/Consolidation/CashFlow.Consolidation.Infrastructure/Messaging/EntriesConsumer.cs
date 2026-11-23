using System.Diagnostics;
using System.Text;
using CashFlow.Consolidation.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace CashFlow.Consolidation.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string Exchange = "entries.events";
    public const string Queue = "consolidation.entries";
    public const string DeadLetterExchange = "entries.events.dlx";
    public const string DeadLetterQueue = "consolidation.entries.dlq";

    /// <summary>AMQP URI, for example amqp://user:password@host:5672/.</summary>
    public required string Uri { get; init; }
}

/// <summary>
/// Consumes the entry events with manual acknowledgements:
/// ack after the balance transaction commits; permanently invalid messages go to the dead-letter queue;
/// transient failures (for example the database is down) put the message back in the queue after a pause,
/// so no event is lost while the service recovers.
/// </summary>
public sealed partial class EntriesConsumer(RabbitMqOptions options, IServiceScopeFactory scopes, ILogger<EntriesConsumer> logger)
    : BackgroundService
{
    private const ushort Prefetch = 50;
    private const int DispatchConcurrency = 8;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DepthSampling = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogConnectionFailed(ex, ReconnectDelay);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = new Uri(options.Uri),
            AutomaticRecoveryEnabled = true,
            ConsumerDispatchConcurrency = DispatchConcurrency,
        };

        await using var connection = await factory.CreateConnectionAsync(stoppingToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        await DeclareTopologyAsync(channel, stoppingToken);
        await channel.BasicQosAsync(0, Prefetch, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) => HandleAsync(channel, delivery, stoppingToken);
        await channel.BasicConsumeAsync(RabbitMqOptions.Queue, autoAck: false, consumer, stoppingToken);

        // Queue depth is the leading indicator of lag: sample it while the consumer runs.
        while (!stoppingToken.IsCancellationRequested)
        {
            var queue = await channel.QueueDeclarePassiveAsync(RabbitMqOptions.Queue, stoppingToken);
            var deadLetter = await channel.QueueDeclarePassiveAsync(RabbitMqOptions.DeadLetterQueue, stoppingToken);
            ConsolidationTelemetry.SetQueueDepths(queue.MessageCount, deadLetter.MessageCount);

            await Task.Delay(DepthSampling, stoppingToken);
        }
    }

    /// <summary>Idempotent: also run by the tests, so both sides agree on the queue layout.</summary>
    public static async Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(RabbitMqOptions.Exchange, ExchangeType.Topic, durable: true, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(RabbitMqOptions.DeadLetterExchange, ExchangeType.Fanout, durable: true, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            RabbitMqOptions.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = "quorum" }, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(RabbitMqOptions.DeadLetterQueue, RabbitMqOptions.DeadLetterExchange, string.Empty, cancellationToken: cancellationToken);

        await channel.QueueDeclareAsync(
            RabbitMqOptions.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum", // replicated across the broker nodes; survives the loss of one
                ["x-dead-letter-exchange"] = RabbitMqOptions.DeadLetterExchange,
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(RabbitMqOptions.Queue, RabbitMqOptions.Exchange, "entry.*", cancellationToken: cancellationToken);
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken stoppingToken)
    {
        // Continue the trace started by the request that registered the entry (context travels in the message headers).
        ActivityContext.TryParse(TraceParentOf(delivery), null, out var parent);
        using var activity = ConsolidationTelemetry.Source.StartActivity("entries.events process", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", RabbitMqOptions.Queue);
        activity?.SetTag("messaging.operation.type", "process");
        activity?.SetTag("messaging.message.id", delivery.BasicProperties.MessageId);

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<EntryEventApplier>();

            var applied = await handler.HandleAsync(delivery.Body, stoppingToken);
            Record(applied ? "applied" : "duplicate");
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
        }
        catch (InvalidEventException ex)
        {
            LogInvalidEvent(ex);
            Record("invalid");
            activity?.SetStatus(ActivityStatusCode.Error, "invalid event");
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down: leave the message unacknowledged; the broker redelivers it to the next consumer.
        }
        catch (Exception ex)
        {
            LogTransientFailure(ex, RetryDelay);
            Record("retry");
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await Task.Delay(RetryDelay, stoppingToken);
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
        }
    }

    private static string? TraceParentOf(BasicDeliverEventArgs delivery) =>
        delivery.BasicProperties.Headers?.TryGetValue("traceparent", out var value) == true && value is byte[] bytes
            ? Encoding.UTF8.GetString(bytes)
            : null;

    private static void Record(string result) =>
        ConsolidationTelemetry.EventsProcessed.Add(1, new KeyValuePair<string, object?>("result", result));

    [LoggerMessage(Level = LogLevel.Warning, Message = "RabbitMQ consumer failed; reconnecting in {Delay}.")]
    private partial void LogConnectionFailed(Exception exception, TimeSpan delay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Invalid event sent to the dead-letter queue.")]
    private partial void LogInvalidEvent(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not process an event; it returns to the queue after {Delay}.")]
    private partial void LogTransientFailure(Exception exception, TimeSpan delay);
}
