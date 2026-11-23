using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CashFlow.Consolidation.Application;
using CashFlow.Consolidation.Infrastructure.Persistence;
using CashFlow.Entries.Domain;
using CashFlow.Entries.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using EntriesApp = CashFlow.Entries.Application;

namespace CashFlow.Consolidation.Integration.Tests;

/// <summary>
/// Real PostgreSQL on both sides. Entries are registered in the Entries database, their events are applied to the
/// Consolidation database the way the consumer does, and then things are broken on purpose to see the job notice.
/// </summary>
[Collection(SharedInfrastructure.Name)]
public sealed class ReconciliationTests(InfrastructureFixture infra)
{
    private const string Actor = "reconciliation-test";
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MerchantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static DateOnly Day => EntriesApp.MerchantCalendar.Today(DateTimeOffset.UtcNow).AddDays(-1);

    /// <summary>Reads the totals straight from the Entries database, which is what the Entries endpoint does.</summary>
    private sealed class EntriesDatabaseSource(string connection) : IEntriesTotalsSource
    {
        public async Task<IReadOnlyList<DailyTotal>> GetAsync(
            DateOnly fromDate, DateOnly toDate, DateTimeOffset registeredBefore, CancellationToken cancellationToken)
        {
            await using var db = new EntriesDbContext(new DbContextOptionsBuilder<EntriesDbContext>().UseNpgsql(connection).Options);
            var totals = await new ReconciliationQueries(db).GetDailyTotalsAsync(fromDate, toDate, registeredBefore, cancellationToken);

            return [.. totals.Select(t => new DailyTotal(t.MerchantId, t.Date, t.Credits, t.Debits))];
        }
    }

    private sealed class Scenario
    {
        public required string EntriesConnection { get; init; }

        public required string ConsolidationConnection { get; init; }

        public required List<OutboxMessage> Events { get; init; }

        public IEntriesTotalsSource Source => new EntriesDatabaseSource(EntriesConnection);
    }

    /// <summary>Merchant A: credit 100, debit 30, credit 20 (one day). Merchant B: credit 50.</summary>
    private async Task<Scenario> StartAsync(bool applyEvents = true)
    {
        var entriesConnection = await infra.CreateEntriesDatabaseAsync();
        var consolidationConnection = await infra.CreateConsolidationDatabaseAsync();

        await using (var db = new EntriesDbContext(new DbContextOptionsBuilder<EntriesDbContext>().UseNpgsql(entriesConnection).Options))
        {
            var register = new EntriesApp.RegisterEntryHandler(new EntryRepository(db), new EfUnitOfWork(db), TimeProvider.System);
            foreach (var (merchant, type, amount) in new[]
            {
                (MerchantA, EntryType.Credit, 100m), (MerchantA, EntryType.Debit, 30m),
                (MerchantA, EntryType.Credit, 20m), (MerchantB, EntryType.Credit, 50m),
            })
            {
                await register.HandleAsync(new EntriesApp.RegisterEntryCommand(merchant, Actor, type, amount, Day, "test"), default);
            }
        }

        await using var check = new EntriesDbContext(new DbContextOptionsBuilder<EntriesDbContext>().UseNpgsql(entriesConnection).Options);
        var events = await check.Outbox.OrderBy(m => m.OccurredAt).ToListAsync();

        var scenario = new Scenario { EntriesConnection = entriesConnection, ConsolidationConnection = consolidationConnection, Events = events };
        if (applyEvents)
        {
            await ApplyAsync(scenario, events);
        }

        return scenario;
    }

    private static async Task ApplyAsync(Scenario scenario, IEnumerable<OutboxMessage> events)
    {
        await using var db = InfrastructureFixture.NewContext(scenario.ConsolidationConnection);
        var store = new CashFlow.Consolidation.Infrastructure.Persistence.EfBalanceStore(db);
        foreach (var message in events)
        {
            await store.ApplyAsync(EntryEvent.Parse(Encoding.UTF8.GetBytes(message.Payload)), DateTimeOffset.UtcNow, default);
        }
    }

    private static async Task<ReconciliationReport> ReconcileAsync(Scenario scenario, TimeSpan? grace = null)
    {
        await using var db = InfrastructureFixture.NewContext(scenario.ConsolidationConnection);
        var reconciler = new Reconciler(
            scenario.Source, new CashFlow.Consolidation.Infrastructure.Reconciliation.EfReconciliationStore(db), TimeProvider.System);

        return await reconciler.RunAsync(Day.AddDays(-2), Day, grace ?? TimeSpan.Zero, default);
    }

    [Fact]
    public async Task WhenEverythingIsApplied_NothingDiverges()
    {
        var scenario = await StartAsync();

        var report = await ReconcileAsync(scenario);

        Assert.True(report.IsConsistent, string.Join("; ", report.Divergences));
    }

    [Fact]
    public async Task AnEventThatNeverArrived_IsFound_AndTheProjectionStillMatchesItsOwnLog()
    {
        var scenario = await StartAsync(applyEvents: false);
        await ApplyAsync(scenario, scenario.Events.Take(2)); // the third and fourth events are lost on the way

        var report = await ReconcileAsync(scenario);

        Assert.NotEmpty(report.Divergences);
        Assert.All(report.Divergences, d => Assert.Equal(DivergenceKind.EntriesVsEvents, d.Kind));
        Assert.DoesNotContain(report.Divergences, d => d.Kind == DivergenceKind.EventsVsBalances); // the balance is honest about what it received
    }

    [Fact]
    public async Task ATamperedBalance_IsFound_AndRebuildingFixesIt()
    {
        var scenario = await StartAsync();
        await using (var db = InfrastructureFixture.NewContext(scenario.ConsolidationConnection))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE daily_balances SET credits = credits + 5 WHERE merchant_id = {MerchantA}");
        }

        var broken = await ReconcileAsync(scenario);

        var drift = Assert.Single(broken.Divergences);
        Assert.Equal(DivergenceKind.EventsVsBalances, drift.Kind);
        Assert.Equal(MerchantA, drift.MerchantId);
        Assert.Equal(120m, drift.ExpectedCredits); // what the log adds up to
        Assert.Equal(125m, drift.ActualCredits);   // what the stored balance says

        await using (var db = InfrastructureFixture.NewContext(scenario.ConsolidationConnection))
        {
            await new CashFlow.Consolidation.Infrastructure.Persistence.EfBalanceStore(db)
                .RebuildAsync(MerchantA, Day, Day, DateTimeOffset.UtcNow, default);
        }

        Assert.True((await ReconcileAsync(scenario)).IsConsistent);
    }

    [Fact]
    public async Task ABalanceWithNoEventsBehindIt_IsFound()
    {
        var scenario = await StartAsync();
        await using (var db = InfrastructureFixture.NewContext(scenario.ConsolidationConnection))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO daily_balances (merchant_id, date, credits, debits, updated_at)
                VALUES ({Guid.NewGuid()}, {Day}, 999, 0, {DateTimeOffset.UtcNow})
                """);
        }

        var report = await ReconcileAsync(scenario);

        var drift = Assert.Single(report.Divergences);
        Assert.Equal(DivergenceKind.EventsVsBalances, drift.Kind);
        Assert.Equal(0m, drift.ExpectedCredits);
        Assert.Equal(999m, drift.ActualCredits);
    }

    [Fact]
    public async Task AnEventForAnEntryThatDoesNotExist_IsFound()
    {
        var scenario = await StartAsync();
        var phantom = $$"""
            {"eventId":"{{Guid.NewGuid()}}","eventType":"EntryRegistered","schemaVersion":1,"occurredAt":"{{DateTimeOffset.UtcNow:O}}",
             "merchantId":"{{Guid.NewGuid()}}","entryId":"{{Guid.NewGuid()}}","type":"Credit","amount":77.00,
             "entryDate":"{{Day:yyyy-MM-dd}}","originalEntryId":null}
            """;
        await using (var db = InfrastructureFixture.NewContext(scenario.ConsolidationConnection))
        {
            await new CashFlow.Consolidation.Infrastructure.Persistence.EfBalanceStore(db)
                .ApplyAsync(EntryEvent.Parse(Encoding.UTF8.GetBytes(phantom)), DateTimeOffset.UtcNow, default);
        }

        var report = await ReconcileAsync(scenario);

        var divergence = Assert.Single(report.Divergences);
        Assert.Equal(DivergenceKind.EntriesVsEvents, divergence.Kind);
        Assert.Equal(0m, divergence.ExpectedCredits);
        Assert.Equal(77m, divergence.ActualCredits);
    }

    [Fact]
    public async Task EntriesYoungerThanTheGrace_AreNotCompared_SoAnEventStillInTransitIsNotAFalseAlarm()
    {
        var scenario = await StartAsync(applyEvents: false); // nothing applied yet: every event is "in transit"

        var report = await ReconcileAsync(scenario, grace: TimeSpan.FromHours(1));

        Assert.True(report.IsConsistent);
    }

    // ---- the administrative endpoint -----------------------------------------------------------------------------

    private static HttpRequestMessage Reconcile(string scopes)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/balances/reconcile") { Content = JsonContent.Create(new { }) };
        request.Headers.Authorization = TestAuth.Bearer(merchant: null, scopes);

        return request;
    }

    [Fact]
    public async Task TheEndpoint_ReportsDivergences_ToTheAdministrativeScopeOnly()
    {
        var scenario = await StartAsync();
        await using (var db = InfrastructureFixture.NewContext(scenario.ConsolidationConnection))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE daily_balances SET debits = debits + 1 WHERE merchant_id = {MerchantB}");
        }

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", scenario.ConsolidationConnection);
            builder.UseSetting("RabbitMq:Uri", "amqp://nobody:nobody@127.0.0.1:1/");
            builder.UseSetting("Redis:ConnectionString", infra.RedisConnectionString);
            TestAuth.Configure(builder);
            builder.ConfigureTestServices(services => services.AddSingleton<IEntriesTotalsSource>(scenario.Source));
        });
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Reconcile("balances:read"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/v1/balances/reconcile", new { })).StatusCode);

        var response = await client.SendAsync(Reconcile("balances:rebuild"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("consistent").GetBoolean());
        Assert.Equal(1, body.GetProperty("divergenceCount").GetInt32());
        var divergence = body.GetProperty("divergences")[0];
        Assert.Equal("EventsVsBalances", divergence.GetProperty("kind").GetString());
        Assert.Equal(MerchantB, divergence.GetProperty("merchantId").GetGuid());
    }
}
