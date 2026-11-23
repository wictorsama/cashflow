using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using CashFlow.Entries.Application;
using CashFlow.Entries.Domain;
using CashFlow.Entries.Infrastructure.Messaging;
using CashFlow.Entries.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

namespace CashFlow.Entries.Integration.Tests;

public sealed class OutboxTests(InfrastructureFixture infra) : IClassFixture<InfrastructureFixture>
{
    private static readonly Guid Merchant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Actor = "user-1";

    private sealed class FactoryFor(string connectionString) : IDbContextFactory<EntriesDbContext>
    {
        public EntriesDbContext CreateDbContext() => InfrastructureFixture.NewContext(connectionString);
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public ConcurrentBag<Guid> Published { get; } = [];

        public bool Fail { get; set; }

        public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("Broker unavailable.");
            }

            await Task.Delay(20, cancellationToken);
            Published.Add(message.Id);
        }
    }

    private static (RegisterEntryHandler Register, ReverseEntryHandler Reverse) Handlers(EntriesDbContext db)
    {
        var clock = TimeProvider.System;
        var repository = new EntryRepository(db);
        var unitOfWork = new EfUnitOfWork(db);
        return (new RegisterEntryHandler(repository, unitOfWork, clock), new ReverseEntryHandler(repository, unitOfWork, clock));
    }

    private static RegisterEntryCommand SomeEntry(decimal amount = 25.50m) =>
        new(Merchant, Actor, EntryType.Credit, amount, MerchantCalendar.Today(DateTimeOffset.UtcNow), "Sale");

    [Fact]
    public async Task Register_StoresEntryAndOutboxEventTogether()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var db = InfrastructureFixture.NewContext(connection);

        var dto = await Handlers(db).Register.HandleAsync(SomeEntry(), default);

        await using var check = InfrastructureFixture.NewContext(connection);
        var entry = await check.Entries.SingleAsync();
        var message = await check.Outbox.SingleAsync();

        Assert.Equal(dto.Id, entry.Id);
        Assert.Equal(25.50m, entry.Amount);
        Assert.Equal(Actor, entry.CreatedBy);
        Assert.Null(message.PublishedAt);
        Assert.Equal("EntryRegistered", message.Type);

        using var payload = JsonDocument.Parse(message.Payload);
        var root = payload.RootElement;
        Assert.Equal(message.Id, root.GetProperty("eventId").GetGuid());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(dto.Id, root.GetProperty("entryId").GetGuid());
        Assert.Equal("Credit", root.GetProperty("type").GetString());
        Assert.Equal(Actor, root.GetProperty("createdBy").GetString()); // the event says who did it
        Assert.Equal(25.50m, root.GetProperty("amount").GetDecimal());
        Assert.Equal(dto.EntryDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), root.GetProperty("entryDate").GetString());
    }

    [Fact]
    public async Task Reverse_StoresCompensatingEntryAndMarksOriginalWithItsEvent()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var db = InfrastructureFixture.NewContext(connection);
        var (register, reverse) = Handlers(db);
        var original = await register.HandleAsync(SomeEntry(), default);

        var reversal = await reverse.HandleAsync(new ReverseEntryCommand(Merchant, Actor, original.Id, "Wrong value"), default);

        await using var check = InfrastructureFixture.NewContext(connection);
        Assert.Equal(reversal.Id, (await check.Entries.SingleAsync(e => e.Id == original.Id)).ReversedBy);
        Assert.Equal(2, await check.Outbox.CountAsync());
        Assert.Equal(1, await check.Outbox.CountAsync(m => m.Type == "EntryReversed"));
    }

    [Fact]
    public async Task Reverse_TwoRequestsLoadTheSameEntry_DatabaseRejectsTheSecondReversal()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var setup = InfrastructureFixture.NewContext(connection);
        var original = await Handlers(setup).Register.HandleAsync(SomeEntry(), default);

        // Both requests read the entry before either one saves, so the domain rule cannot see the other.
        await using var dbA = InfrastructureFixture.NewContext(connection);
        await using var dbB = InfrastructureFixture.NewContext(connection);
        var entryA = await new EntryRepository(dbA).FindAsync(Merchant, original.Id, default);
        var entryB = await new EntryRepository(dbB).FindAsync(Merchant, original.Id, default);

        var reversalA = entryA!.Reverse(null, Actor, DateTimeOffset.UtcNow);
        var reversalB = entryB!.Reverse(null, Actor, DateTimeOffset.UtcNow);

        await new EntryRepository(dbA).AddAsync(reversalA, default);
        await new EfUnitOfWork(dbA).SaveChangesAsync(default);

        await new EntryRepository(dbB).AddAsync(reversalB, default);
        await Assert.ThrowsAsync<DbUpdateException>(() => new EfUnitOfWork(dbB).SaveChangesAsync(default));

        await using var check = InfrastructureFixture.NewContext(connection);
        Assert.Equal(1, await check.Entries.CountAsync(e => e.ReversalOf == original.Id));
        Assert.Equal(2, await check.Outbox.CountAsync());
    }

    [Fact]
    public async Task Register_WorksWhileBrokerIsDown_AndEventIsPublishedLater()
    {
        var connection = await infra.CreateDatabaseAsync();
        var publisher = new RecordingPublisher { Fail = true };
        var processor = new OutboxProcessor(new FactoryFor(connection), publisher, TimeProvider.System);

        await using (var db = InfrastructureFixture.NewContext(connection))
        {
            // Entries must stay available when the consolidation side is down.
            await Handlers(db).Register.HandleAsync(SomeEntry(), default);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => processor.ProcessBatchAsync(10, default));

        await using (var check = InfrastructureFixture.NewContext(connection))
        {
            Assert.Equal(1, await check.Outbox.CountAsync(m => m.PublishedAt == null));
        }

        publisher.Fail = false;
        Assert.Equal(1, await processor.ProcessBatchAsync(10, default));
        Assert.Single(publisher.Published);
        Assert.Equal(0, await processor.ProcessBatchAsync(10, default));
    }

    [Fact]
    public async Task TwoRelaysAtOnce_PublishEachEventOnce()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using (var db = InfrastructureFixture.NewContext(connection))
        {
            var register = Handlers(db).Register;
            for (var i = 0; i < 30; i++)
            {
                await register.HandleAsync(SomeEntry(10 + i), default);
            }
        }

        var publisher = new RecordingPublisher();
        var relayA = new OutboxProcessor(new FactoryFor(connection), publisher, TimeProvider.System);
        var relayB = new OutboxProcessor(new FactoryFor(connection), publisher, TimeProvider.System);

        var counts = await Task.WhenAll(relayA.ProcessBatchAsync(15, default), relayB.ProcessBatchAsync(15, default));

        Assert.Equal(30, counts.Sum());
        Assert.Equal(30, publisher.Published.Distinct().Count());
        Assert.Equal(30, publisher.Published.Count);
    }

    [Fact]
    public async Task RabbitMqPublisher_DeliversEventToBoundQueue()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var db = InfrastructureFixture.NewContext(connection);
        var dto = await Handlers(db).Register.HandleAsync(SomeEntry(), default);

        await using var publisher = new RabbitMqEventPublisher(new RabbitMqOptions { Uri = infra.RabbitMqUri });

        // Declares the exchange, then binds a queue the way the Consolidation service will.
        await publisher.PublishAsync(
            new OutboxMessage { Id = Guid.NewGuid(), Type = "EntryRegistered", Payload = "{}", OccurredAt = DateTimeOffset.UtcNow },
            default);

        var factory = new ConnectionFactory { Uri = new Uri(infra.RabbitMqUri) };
        await using var consumerConnection = await factory.CreateConnectionAsync();
        await using var channel = await consumerConnection.CreateChannelAsync();
        var queue = (await channel.QueueDeclareAsync("test.consolidation", durable: true, exclusive: false, autoDelete: false)).QueueName;
        await channel.QueueBindAsync(queue, RabbitMqOptions.Exchange, "entry.*");

        var processor = new OutboxProcessor(new FactoryFor(connection), publisher, TimeProvider.System);
        Assert.Equal(1, await processor.ProcessBatchAsync(10, default));

        BasicGetResult? received = null;
        for (var attempt = 0; attempt < 20 && received is null; attempt++)
        {
            received = await channel.BasicGetAsync(queue, autoAck: true);
            if (received is null)
            {
                await Task.Delay(100);
            }
        }

        Assert.NotNull(received);
        Assert.Equal("entry.registered", received.RoutingKey);
        using var body = JsonDocument.Parse(Encoding.UTF8.GetString(received.Body.ToArray()));
        Assert.Equal(dto.Id, body.RootElement.GetProperty("entryId").GetGuid());
        Assert.Equal(DeliveryModes.Persistent, received.BasicProperties.DeliveryMode);
    }
}
