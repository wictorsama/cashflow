using System.Text;
using System.Text.Json;
using CashFlow.Consolidation.Application;
using CashFlow.Consolidation.Infrastructure.Caching;
using CashFlow.Consolidation.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace CashFlow.Consolidation.Integration.Tests;

[Collection(SharedInfrastructure.Name)]
public sealed class BalanceTests(InfrastructureFixture infra)
{
    private static readonly DateOnly D1 = new(2026, 11, 10);
    private static readonly DateOnly D2 = new(2026, 11, 11);
    private static readonly DateOnly D3 = new(2026, 11, 12);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static byte[] Event(Guid merchant, string type, decimal amount, DateOnly date, Guid? eventId = null) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new EntryEvent(
                eventId ?? Guid.NewGuid(), "EntryRegistered", 1, DateTimeOffset.UtcNow, merchant, Guid.NewGuid(), type, amount, date, null),
            JsonOptions);

    private sealed class Services(string connection, IBalanceCache cache) : IAsyncDisposable
    {
        private readonly List<ConsolidationDbContext> _contexts = [];

        /// <summary>Each call gets its own database context, like each message handled by the consumer does.</summary>
        public EntryEventApplier NewApplier() => new(NewStore(), cache, TimeProvider.System);

        public GetBalanceHandler NewReader() => new(NewStore(), cache);

        public GetBalanceRangeHandler NewRangeReader() => new(NewStore());

        public RebuildBalancesHandler NewRebuilder() => new(NewStore(), cache, TimeProvider.System);

        public ConsolidationDbContext NewDb()
        {
            var db = InfrastructureFixture.NewContext(connection);
            _contexts.Add(db);
            return db;
        }

        private EfBalanceStore NewStore() => new(NewDb());

        public async ValueTask DisposeAsync()
        {
            foreach (var db in _contexts)
            {
                await db.DisposeAsync();
            }
        }
    }

    private async Task<Services> StartAsync(IBalanceCache? cache = null)
    {
        var redis = await ConnectionMultiplexer.ConnectAsync(infra.RedisConnectionString);

        return new Services(
            await infra.CreateConsolidationDatabaseAsync(),
            cache ?? new RedisBalanceCache(redis, NullLogger<RedisBalanceCache>.Instance));
    }

    [Fact]
    public async Task Apply_CreditsAndDebits_ComputeDayResult()
    {
        await using var s = await StartAsync();
        var merchant = Guid.NewGuid();

        await s.NewApplier().HandleAsync(Event(merchant, "Credit", 100m, D1), default);
        await s.NewApplier().HandleAsync(Event(merchant, "Credit", 50.50m, D1), default);
        await s.NewApplier().HandleAsync(Event(merchant, "Debit", 30m, D1), default);

        var balance = await s.NewReader().HandleAsync(merchant, D1, default);

        Assert.Equal(150.50m, balance.Credits);
        Assert.Equal(30m, balance.Debits);
        Assert.Equal(120.50m, balance.DayResult);
        Assert.Equal(120.50m, balance.AccumulatedBalance);
    }

    [Fact]
    public async Task Apply_SameEventDeliveredTwice_CountsOnce()
    {
        await using var s = await StartAsync();
        var merchant = Guid.NewGuid();
        var message = Event(merchant, "Credit", 100m, D1);

        Assert.True(await s.NewApplier().HandleAsync(message, default));
        Assert.False(await s.NewApplier().HandleAsync(message, default));

        Assert.Equal(100m, (await s.NewReader().HandleAsync(merchant, D1, default)).Credits);
    }

    [Fact]
    public async Task Apply_SameEventDeliveredConcurrently_CountsOnce()
    {
        await using var s = await StartAsync();
        var merchant = Guid.NewGuid();
        var message = Event(merchant, "Credit", 100m, D1);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => s.NewApplier().HandleAsync(message, default)));

        Assert.Single(results, applied => applied);
        Assert.Equal(100m, (await s.NewReader().HandleAsync(merchant, D1, default)).Credits);
    }

    [Fact]
    public async Task Apply_ManyConcurrentEventsOnTheSameDay_LoseNoUpdate()
    {
        await using var s = await StartAsync();
        var merchant = Guid.NewGuid();

        // 100 events of 10.00 credit and 100 of 4.00 debit, 16 at a time, all racing for the same daily row.
        using var gate = new SemaphoreSlim(16);
        async Task ApplyAsync(string type, decimal amount)
        {
            await gate.WaitAsync();
            try
            {
                await s.NewApplier().HandleAsync(Event(merchant, type, amount, D1), default);
            }
            finally
            {
                gate.Release();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 100).SelectMany(_ => new[] { ApplyAsync("Credit", 10m), ApplyAsync("Debit", 4m) }));

        var balance = await s.NewReader().HandleAsync(merchant, D1, default);
        Assert.Equal(1000m, balance.Credits);
        Assert.Equal(400m, balance.Debits);
        Assert.Equal(600m, balance.DayResult);
    }

    [Fact]
    public async Task Balance_AccumulatesAcrossDays_AndRetroactiveEventUpdatesLaterDays()
    {
        await using var s = await StartAsync();
        var merchant = Guid.NewGuid();
        await s.NewApplier().HandleAsync(Event(merchant, "Credit", 100m, D1), default);
        await s.NewApplier().HandleAsync(Event(merchant, "Debit", 30m, D2), default);

        Assert.Equal(70m, (await s.NewReader().HandleAsync(merchant, D2, default)).AccumulatedBalance);

        // An entry dated in the past arrives late: the accumulated balance of the later day follows.
        await s.NewApplier().HandleAsync(Event(merchant, "Credit", 20m, D1), default);

        var d2 = await s.NewReader().HandleAsync(merchant, D2, default);
        Assert.Equal(-30m, d2.DayResult);
        Assert.Equal(90m, d2.AccumulatedBalance);
    }

    [Fact]
    public async Task Balance_OfDayWithoutEntries_IsZeroWithTheAccumulatedFromBefore()
    {
        await using var s = await StartAsync();
        var merchant = Guid.NewGuid();
        await s.NewApplier().HandleAsync(Event(merchant, "Credit", 100m, D1), default);

        var d3 = await s.NewReader().HandleAsync(merchant, D3, default);

        Assert.Equal(0m, d3.DayResult);
        Assert.Equal(100m, d3.AccumulatedBalance);
    }

    [Fact]
    public async Task Balance_IsIsolatedPerMerchant()
    {
        await using var s = await StartAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await s.NewApplier().HandleAsync(Event(a, "Credit", 100m, D1), default);
        await s.NewApplier().HandleAsync(Event(b, "Credit", 7m, D1), default);

        Assert.Equal(100m, (await s.NewReader().HandleAsync(a, D1, default)).Credits);
        Assert.Equal(7m, (await s.NewReader().HandleAsync(b, D1, default)).Credits);
    }

    [Fact]
    public async Task Range_ReturnsOpeningClosingAndRunningBalance()
    {
        await using var s = await StartAsync();
        var merchant = Guid.NewGuid();
        await s.NewApplier().HandleAsync(Event(merchant, "Credit", 100m, D1), default);
        await s.NewApplier().HandleAsync(Event(merchant, "Debit", 30m, D2), default);
        await s.NewApplier().HandleAsync(Event(merchant, "Credit", 5m, D3), default);

        var range = await s.NewRangeReader().HandleAsync(merchant, D2, D3, default);

        Assert.Equal(100m, range.OpeningBalance);
        Assert.Equal(75m, range.ClosingBalance);
        Assert.Equal(2, range.Days.Count);
        Assert.Equal(70m, range.Days[0].AccumulatedBalance);
        Assert.Equal(75m, range.Days[1].AccumulatedBalance);
    }

    [Fact]
    public async Task Rebuild_RestoresBalancesFromTheEventLog()
    {
        await using var s = await StartAsync();
        var merchant = Guid.NewGuid();
        await s.NewApplier().HandleAsync(Event(merchant, "Credit", 100m, D1), default);
        await s.NewApplier().HandleAsync(Event(merchant, "Debit", 40m, D1), default);
        await s.NewApplier().HandleAsync(Event(merchant, "Credit", 9m, D2), default);

        // Simulate a corrupted projection.
        await s.NewDb().Database.ExecuteSqlInterpolatedAsync($"UPDATE daily_balances SET credits = 999999, debits = 0");
        Assert.Equal(999999m, (await s.NewRangeReader().HandleAsync(merchant, D1, D1, default)).Days[0].Credits);

        var days = await s.NewRebuilder().HandleAsync(new RebuildCommand(merchant, D1, D2), default);

        Assert.Equal(2, days);
        var range = await s.NewRangeReader().HandleAsync(merchant, D1, D2, default);
        Assert.Equal(100m, range.Days[0].Credits);
        Assert.Equal(40m, range.Days[0].Debits);
        Assert.Equal(9m, range.Days[1].Credits);
        Assert.Equal(69m, range.ClosingBalance);
    }

    [Fact]
    public async Task Rebuild_DoesNotTouchOtherMerchantsOrDays()
    {
        await using var s = await StartAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await s.NewApplier().HandleAsync(Event(a, "Credit", 10m, D1), default);
        await s.NewApplier().HandleAsync(Event(a, "Credit", 20m, D3), default);
        await s.NewApplier().HandleAsync(Event(b, "Credit", 77m, D1), default);

        await s.NewRebuilder().HandleAsync(new RebuildCommand(a, D1, D1), default);

        Assert.Equal(20m, (await s.NewReader().HandleAsync(a, D3, default)).Credits);
        Assert.Equal(77m, (await s.NewReader().HandleAsync(b, D1, default)).Credits);
    }

    [Fact]
    public async Task Cache_IsFilledOnMiss_AndInvalidatedByANewEvent()
    {
        var redis = await ConnectionMultiplexer.ConnectAsync(infra.RedisConnectionString);
        await using var s = await StartAsync(new RedisBalanceCache(redis, NullLogger<RedisBalanceCache>.Instance));
        var merchant = Guid.NewGuid();
        await s.NewApplier().HandleAsync(Event(merchant, "Credit", 100m, D1), default);

        await s.NewReader().HandleAsync(merchant, D1, default);

        // The second read is served by Redis: change the database behind its back and it must not notice.
        await s.NewDb().Database.ExecuteSqlInterpolatedAsync($"UPDATE daily_balances SET credits = 1 WHERE merchant_id = {merchant}");
        Assert.Equal(100m, (await s.NewReader().HandleAsync(merchant, D1, default)).Credits);

        // A new event invalidates the cache, so the next read sees the database again.
        await s.NewApplier().HandleAsync(Event(merchant, "Credit", 5m, D1), default);
        Assert.Equal(6m, (await s.NewReader().HandleAsync(merchant, D1, default)).Credits);
    }

    [Fact]
    public async Task Cache_Unavailable_ReadsStillWorkFromTheDatabase()
    {
        var deadRedis = await ConnectionMultiplexer.ConnectAsync(
            "localhost:1,abortConnect=false,connectTimeout=200,asyncTimeout=200,syncTimeout=200");
        await using var s = await StartAsync(new RedisBalanceCache(deadRedis, NullLogger<RedisBalanceCache>.Instance));
        var merchant = Guid.NewGuid();

        await s.NewApplier().HandleAsync(Event(merchant, "Credit", 100m, D1), default);
        var balance = await s.NewReader().HandleAsync(merchant, D1, default);

        Assert.Equal(100m, balance.Credits);
    }

    [Fact]
    public async Task Invalid_UnsupportedEvent_IsRejectedBeforeTouchingTheDatabase()
    {
        await using var s = await StartAsync();
        var merchant = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidEventException>(() =>
            s.NewApplier().HandleAsync(Encoding.UTF8.GetBytes("""{"schemaVersion":99}"""), default));

        Assert.Equal(0, await s.NewDb().EventLog.CountAsync(e => e.MerchantId == merchant));
    }
}
