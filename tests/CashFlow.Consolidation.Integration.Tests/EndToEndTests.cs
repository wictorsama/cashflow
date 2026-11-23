using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CashFlow.Consolidation.Infrastructure.Messaging;
using CashFlow.Entries.Application;
using CashFlow.Entries.Domain;
using CashFlow.Entries.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using EntriesMessaging = CashFlow.Entries.Infrastructure.Messaging;

namespace CashFlow.Consolidation.Integration.Tests;

/// <summary>The whole flow: Entries writes and publishes through the outbox, RabbitMQ delivers, Consolidation updates the balance.</summary>
[Collection(SharedInfrastructure.Name)]
public sealed class EndToEndTests(InfrastructureFixture infra)
{
    private const string Actor = "user-1";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static DateOnly PastDay(int daysAgo) => MerchantCalendar.Today(DateTimeOffset.UtcNow).AddDays(-daysAgo);

    private sealed class FactoryFor(string connectionString) : IDbContextFactory<EntriesDbContext>
    {
        public EntriesDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<EntriesDbContext>().UseNpgsql(connectionString).Options);
    }

    private sealed record Producer(string EntriesConnection, string RabbitUri);

    private async Task<Producer> NewProducerAsync(string rabbitUri) =>
        new(await infra.CreateEntriesDatabaseAsync(), rabbitUri);

    /// <summary>Registers an entry in the Entries service and flushes its outbox to RabbitMQ.</summary>
    private static async Task RegisterAsync(Producer producer, Guid merchant, EntryType type, decimal amount, DateOnly date)
    {
        await using (var db = new FactoryFor(producer.EntriesConnection).CreateDbContext())
        {
            var handler = new RegisterEntryHandler(new EntryRepository(db), new EfUnitOfWork(db), TimeProvider.System);
            await handler.HandleAsync(new RegisterEntryCommand(merchant, Actor, type, amount, date, "e2e"), default);
        }

        await using var publisher = new EntriesMessaging.RabbitMqEventPublisher(new EntriesMessaging.RabbitMqOptions { Uri = producer.RabbitUri });
        var processor = new EntriesMessaging.OutboxProcessor(new FactoryFor(producer.EntriesConnection), publisher, TimeProvider.System);
        await processor.ProcessBatchAsync(100, default);
    }

    private async Task<(WebApplicationFactory<Program> Factory, HttpClient Client)> StartConsolidationAsync(string rabbitUri)
    {
        var connection = await infra.CreateConsolidationDatabaseAsync();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", connection);
            builder.UseSetting("RabbitMq:Uri", rabbitUri);
            builder.UseSetting("Redis:ConnectionString", infra.RedisConnectionString);
            TestAuth.Configure(builder);
        });

        return (factory, factory.CreateClient());
    }

    private static async Task<JsonElement> GetBalanceAsync(HttpClient client, Guid merchant, DateOnly day)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/balances/{day:yyyy-MM-dd}");
        request.Headers.Authorization = TestAuth.Bearer(merchant, "balances:read");
        var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }

    private static async Task<JsonElement> WaitForCreditsAsync(HttpClient client, Guid merchant, DateOnly day, decimal credits)
    {
        var deadline = DateTime.UtcNow + Timeout;
        JsonElement balance;
        do
        {
            balance = await GetBalanceAsync(client, merchant, day);
            if (balance.GetProperty("credits").GetDecimal() == credits)
            {
                return balance;
            }

            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException($"Credits did not reach {credits}; last balance: {balance}.");
    }

    private static async Task<IChannel> OpenChannelAsync(string rabbitUri)
    {
        var connection = await new ConnectionFactory { Uri = new Uri(rabbitUri) }.CreateConnectionAsync();
        return await connection.CreateChannelAsync();
    }

    [Fact]
    public async Task EntryRegistered_AppearsInTheDailyBalance()
    {
        var rabbit = await infra.CreateVirtualHostAsync();
        var (factory, client) = await StartConsolidationAsync(rabbit);
        await using var _ = factory;
        var producer = await NewProducerAsync(rabbit);
        var merchant = Guid.NewGuid();
        var day = PastDay(2);

        await RegisterAsync(producer, merchant, EntryType.Credit, 120m, day);
        await RegisterAsync(producer, merchant, EntryType.Debit, 45m, day);

        var balance = await WaitForCreditsAsync(client, merchant, day, 120m);
        // Both events are flushed before the first wait ends: the debit follows.
        var deadline = DateTime.UtcNow + Timeout;
        while (balance.GetProperty("debits").GetDecimal() != 45m && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            balance = await GetBalanceAsync(client, merchant, day);
        }

        Assert.Equal(75m, balance.GetProperty("dayResult").GetDecimal());
        Assert.Equal(75m, balance.GetProperty("accumulatedBalance").GetDecimal());
    }

    [Fact]
    public async Task ReversedEntry_NetsToZeroInTheDailyBalance()
    {
        var rabbit = await infra.CreateVirtualHostAsync();
        var (factory, client) = await StartConsolidationAsync(rabbit);
        await using var _ = factory;
        var producer = await NewProducerAsync(rabbit);
        var merchant = Guid.NewGuid();
        var day = PastDay(3);

        await RegisterAsync(producer, merchant, EntryType.Credit, 200m, day);
        await WaitForCreditsAsync(client, merchant, day, 200m);

        await using (var db = new FactoryFor(producer.EntriesConnection).CreateDbContext())
        {
            var original = await db.Entries.SingleAsync();
            var handler = new ReverseEntryHandler(new EntryRepository(db), new EfUnitOfWork(db), TimeProvider.System);
            await handler.HandleAsync(new ReverseEntryCommand(merchant, Actor, original.Id, "mistake"), default);
        }

        await using var publisher = new EntriesMessaging.RabbitMqEventPublisher(new EntriesMessaging.RabbitMqOptions { Uri = rabbit });
        await new EntriesMessaging.OutboxProcessor(new FactoryFor(producer.EntriesConnection), publisher, TimeProvider.System)
            .ProcessBatchAsync(100, default);

        var deadline = DateTime.UtcNow + Timeout;
        JsonElement balance;
        do
        {
            await Task.Delay(200);
            balance = await GetBalanceAsync(client, merchant, day);
        }
        while (balance.GetProperty("debits").GetDecimal() != 200m && DateTime.UtcNow < deadline);

        Assert.Equal(0m, balance.GetProperty("dayResult").GetDecimal());
    }

    [Fact]
    public async Task ConsolidationDown_EventsWaitInTheQueueAndAreProcessedOnStart()
    {
        var rabbit = await infra.CreateVirtualHostAsync();
        var producer = await NewProducerAsync(rabbit);
        var merchant = Guid.NewGuid();
        var day = PastDay(1);

        // The queue exists (the service ran before) but no consumer is running now.
        await using (var channel = await OpenChannelAsync(rabbit))
        {
            await EntriesConsumer.DeclareTopologyAsync(channel, default);
        }

        // Entries keeps accepting entries while Consolidation is down.
        for (var i = 0; i < 20; i++)
        {
            await RegisterAsync(producer, merchant, EntryType.Credit, 10m, day);
        }

        var (factory, client) = await StartConsolidationAsync(rabbit);
        await using var _ = factory;

        var balance = await WaitForCreditsAsync(client, merchant, day, 200m);
        Assert.Equal(200m, balance.GetProperty("accumulatedBalance").GetDecimal());
    }

    [Fact]
    public async Task InvalidMessage_GoesToTheDeadLetterQueue_AndDoesNotBlockValidOnes()
    {
        var rabbit = await infra.CreateVirtualHostAsync();
        var (factory, client) = await StartConsolidationAsync(rabbit);
        await using var _ = factory;
        var producer = await NewProducerAsync(rabbit);
        var merchant = Guid.NewGuid();
        var day = PastDay(1);

        await using var channel = await OpenChannelAsync(rabbit);
        await EntriesConsumer.DeclareTopologyAsync(channel, default);
        await channel.BasicPublishAsync(
            RabbitMqOptions.Exchange, "entry.registered", mandatory: false, new BasicProperties(), Encoding.UTF8.GetBytes("this is not json"));

        await RegisterAsync(producer, merchant, EntryType.Credit, 33m, day);
        await WaitForCreditsAsync(client, merchant, day, 33m);

        BasicGetResult? dead = null;
        var deadline = DateTime.UtcNow + Timeout;
        while (dead is null && DateTime.UtcNow < deadline)
        {
            dead = await channel.BasicGetAsync(RabbitMqOptions.DeadLetterQueue, autoAck: true);
            if (dead is null)
            {
                await Task.Delay(200);
            }
        }

        Assert.NotNull(dead);
        Assert.Equal("this is not json", Encoding.UTF8.GetString(dead.Body.ToArray()));
    }

    [Fact]
    public async Task SameMessagePublishedTwice_IsCountedOnce()
    {
        var rabbit = await infra.CreateVirtualHostAsync();
        var (factory, client) = await StartConsolidationAsync(rabbit);
        await using var _ = factory;
        var merchant = Guid.NewGuid();
        var day = PastDay(1);
        var body = JsonSerializer.Serialize(new
        {
            eventId = Guid.NewGuid(),
            eventType = "EntryRegistered",
            schemaVersion = 1,
            occurredAt = DateTimeOffset.UtcNow,
            merchantId = merchant,
            entryId = Guid.NewGuid(),
            type = "Credit",
            amount = 50m,
            entryDate = day,
        });

        await using var channel = await OpenChannelAsync(rabbit);
        await EntriesConsumer.DeclareTopologyAsync(channel, default);
        for (var i = 0; i < 2; i++)
        {
            await channel.BasicPublishAsync(
                RabbitMqOptions.Exchange, "entry.registered", mandatory: false, new BasicProperties(), Encoding.UTF8.GetBytes(body));
        }

        await WaitForCreditsAsync(client, merchant, day, 50m);
        await Task.Delay(1500);

        Assert.Equal(50m, (await GetBalanceAsync(client, merchant, day)).GetProperty("credits").GetDecimal());
    }

    [Fact]
    public async Task Api_ValidatesInput_AndServesRangeAndRebuild()
    {
        var rabbit = await infra.CreateVirtualHostAsync();
        var (factory, client) = await StartConsolidationAsync(rabbit);
        await using var _ = factory;
        var producer = await NewProducerAsync(rabbit);
        var merchant = Guid.NewGuid();
        var day = PastDay(1);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/v1/balances/{day:yyyy-MM-dd}")).StatusCode);

        var badRange = new HttpRequestMessage(HttpMethod.Get, "/v1/balances?from=2024-01-01&to=2026-01-01");
        badRange.Headers.Authorization = TestAuth.Bearer(merchant, "balances:read");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(badRange)).StatusCode);

        await RegisterAsync(producer, merchant, EntryType.Credit, 80m, day);
        await WaitForCreditsAsync(client, merchant, day, 80m);

        var range = new HttpRequestMessage(HttpMethod.Get, $"/v1/balances?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}");
        range.Headers.Authorization = TestAuth.Bearer(merchant, "balances:read");
        var rangeBody = await (await client.SendAsync(range)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(80m, rangeBody.GetProperty("closingBalance").GetDecimal());

        var rebuild = await RebuildAsync(client, merchant, day, "balances:rebuild");
        Assert.Equal(HttpStatusCode.OK, rebuild.StatusCode);
        Assert.Equal(1, (await rebuild.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("rebuiltDays").GetInt32());
        Assert.Equal(80m, (await GetBalanceAsync(client, merchant, day)).GetProperty("credits").GetDecimal());
    }

    [Fact]
    public async Task TraceContext_TravelsFromTheRequestThroughTheBrokerToTheConsumer_AndMetricsAreRecorded()
    {
        var spans = new System.Collections.Concurrent.ConcurrentBag<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("CashFlow.", StringComparison.Ordinal) || source.Name == "Test",
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = spans.Add,
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var processed = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var meters = new System.Diagnostics.Metrics.MeterListener();
        meters.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "cashflow.events.processed")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        meters.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                processed.Add($"{tag.Key}={tag.Value}");
            }
        });
        meters.Start();

        var rabbit = await infra.CreateVirtualHostAsync();
        var (factory, client) = await StartConsolidationAsync(rabbit);
        await using var _ = factory;
        var producer = await NewProducerAsync(rabbit);
        var merchant = Guid.NewGuid();
        var day = PastDay(1);

        using var testSource = new System.Diagnostics.ActivitySource("Test");
        using var request = testSource.StartActivity("request", System.Diagnostics.ActivityKind.Server);
        Assert.NotNull(request);

        await RegisterAsync(producer, merchant, EntryType.Credit, 10m, day);
        await WaitForCreditsAsync(client, merchant, day, 10m);

        var published = Assert.Single(spans, s => s.OperationName == "entries.events publish");
        var consumed = Assert.Single(spans, s => s.OperationName == "entries.events process");

        Assert.Equal(request.TraceId, published.TraceId);
        Assert.Equal(request.TraceId, consumed.TraceId); // one trace across the broker
        Assert.Equal(published.Id, consumed.ParentId);
        Assert.Contains("result=applied", processed);
    }

    [Fact]
    public async Task Readiness_ChecksTheDatabase_AndLivenessChecksNothingElse()
    {
        var rabbit = await infra.CreateVirtualHostAsync();
        var (factory, client) = await StartConsolidationAsync(rabbit);
        await using var _ = factory;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    private static async Task<HttpResponseMessage> RebuildAsync(HttpClient client, Guid merchant, DateOnly day, string? scopes)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/balances/rebuild")
        {
            Content = JsonContent.Create(new { merchantId = merchant, from = day, to = day }),
        };
        if (scopes is not null)
        {
            request.Headers.Authorization = TestAuth.Bearer(Guid.NewGuid(), scopes);
        }

        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Api_EnforcesAuthentication_Scopes_AndMerchantIsolation()
    {
        var rabbit = await infra.CreateVirtualHostAsync();
        var (factory, client) = await StartConsolidationAsync(rabbit);
        await using var _ = factory;
        var producer = await NewProducerAsync(rabbit);
        var owner = Guid.NewGuid();
        var day = PastDay(1);
        await RegisterAsync(producer, owner, EntryType.Credit, 80m, day);
        await WaitForCreditsAsync(client, owner, day, 80m);

        // Rebuild is administrative: no token, or a token without its scope, is refused.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RebuildAsync(client, owner, day, scopes: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await RebuildAsync(client, owner, day, "balances:read entries:write")).StatusCode);

        // A token that is valid but names no merchant cannot read balances.
        var noMerchant = new HttpRequestMessage(HttpMethod.Get, $"/v1/balances/{day:yyyy-MM-dd}");
        noMerchant.Headers.Authorization = TestAuth.Bearer(merchant: null, "balances:read");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(noMerchant)).StatusCode);

        // Reading needs balances:read.
        var wrongScope = new HttpRequestMessage(HttpMethod.Get, $"/v1/balances/{day:yyyy-MM-dd}");
        wrongScope.Headers.Authorization = TestAuth.Bearer(owner, "entries:read");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(wrongScope)).StatusCode);

        // Another merchant, with a perfectly valid token, sees only its own (empty) balance.
        var other = await GetBalanceAsync(client, Guid.NewGuid(), day);
        Assert.Equal(0m, other.GetProperty("credits").GetDecimal());
    }
}
