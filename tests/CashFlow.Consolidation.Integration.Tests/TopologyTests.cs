using System.Text;
using CashFlow.Consolidation.Infrastructure.Messaging;
using CashFlow.Entries.Infrastructure.Persistence;
using RabbitMQ.Client;
using EntriesMessaging = CashFlow.Entries.Infrastructure.Messaging;

namespace CashFlow.Consolidation.Integration.Tests;

/// <summary>
/// The publisher and the consumer both declare the queue. If they ever disagree on a name or an argument, RabbitMQ
/// refuses the second declaration and the whole flow stops: these tests catch that here instead of in production.
/// </summary>
[Collection(SharedInfrastructure.Name)]
public sealed class TopologyTests(InfrastructureFixture infra)
{
    private static OutboxMessage SomeEvent() => new()
    {
        Id = Guid.NewGuid(),
        Type = "EntryRegistered",
        Payload = """{"eventId":"00000000-0000-0000-0000-000000000001","schemaVersion":1}""",
        OccurredAt = DateTimeOffset.UtcNow,
    };

    private static async Task<IChannel> OpenAsync(string uri) =>
        await (await new ConnectionFactory { Uri = new Uri(uri) }.CreateConnectionAsync()).CreateChannelAsync();

    [Fact]
    public async Task AnEventPublishedBeforeTheConsumerEverStarted_WaitsInTheQueue_AndIsNotLost()
    {
        var uri = await infra.CreateVirtualHostAsync(); // a broker where nobody has declared anything yet
        await using var publisher = new EntriesMessaging.RabbitMqEventPublisher(new EntriesMessaging.RabbitMqOptions { Uri = uri });

        await publisher.PublishAsync(SomeEvent(), default); // the Consolidation service has never run

        await using var channel = await OpenAsync(uri);
        var message = await channel.BasicGetAsync(RabbitMqOptions.Queue, autoAck: true);
        Assert.NotNull(message); // before the fix an exchange with no bound queue dropped it silently
        Assert.Contains("EntryRegistered", Encoding.UTF8.GetString(message.Body.Span) + message.BasicProperties.Type, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublisherAndConsumerDeclareTheSameTopology_InEitherOrder()
    {
        var uri = await infra.CreateVirtualHostAsync();
        await using var channel = await OpenAsync(uri);

        // Consumer first, then publisher: no argument mismatch.
        await EntriesConsumer.DeclareTopologyAsync(channel, default);
        await EntriesMessaging.RabbitMqEventPublisher.DeclareConsumerQueueAsync(channel, default);

        // And on another broker, publisher first, then consumer.
        var other = await infra.CreateVirtualHostAsync();
        await using var otherChannel = await OpenAsync(other);
        await otherChannel.ExchangeDeclareAsync(EntriesMessaging.RabbitMqOptions.Exchange, ExchangeType.Topic, durable: true);
        await EntriesMessaging.RabbitMqEventPublisher.DeclareConsumerQueueAsync(otherChannel, default);
        await EntriesConsumer.DeclareTopologyAsync(otherChannel, default);
    }

    [Fact]
    public void TheNamesBothSidesUse_AreTheSame()
    {
        Assert.Equal(RabbitMqOptions.Exchange, EntriesMessaging.RabbitMqOptions.Exchange);
        Assert.Equal(RabbitMqOptions.Queue, EntriesMessaging.RabbitMqOptions.ConsumerQueue);
        Assert.Equal(RabbitMqOptions.DeadLetterExchange, EntriesMessaging.RabbitMqOptions.DeadLetterExchange);
        Assert.Equal(RabbitMqOptions.DeadLetterQueue, EntriesMessaging.RabbitMqOptions.DeadLetterQueue);
    }
}
