using System.Text;
using CashFlow.Consolidation.Application;

namespace CashFlow.Consolidation.Application.Tests;

public class ApplicationTests
{
    private static readonly Guid Merchant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateOnly Day = new(2026, 11, 16);

    private static byte[] Message(
        string type = "Credit", decimal amount = 10m, int schemaVersion = 1, Guid? eventId = null) =>
        Encoding.UTF8.GetBytes($$"""
            {"eventId":"{{eventId ?? Guid.NewGuid()}}","eventType":"EntryRegistered","schemaVersion":{{schemaVersion}},
             "occurredAt":"2026-11-16T12:00:00+00:00","merchantId":"{{Merchant}}","entryId":"{{Guid.NewGuid()}}",
             "type":"{{type}}","amount":{{amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
             "entryDate":"2026-11-16","originalEntryId":null}
            """);

    private sealed class FakeStore : IBalanceStore
    {
        public int DailyReads { get; private set; }

        public bool Duplicate { get; set; }

        public Task<bool> ApplyAsync(EntryEvent entryEvent, DateTimeOffset receivedAt, CancellationToken cancellationToken) =>
            Task.FromResult(!Duplicate);

        public Task<DailyBalanceDto> GetDailyAsync(Guid merchantId, DateOnly day, CancellationToken cancellationToken)
        {
            DailyReads++;
            return Task.FromResult(new DailyBalanceDto(merchantId, day, 100m, 40m, 60m, 60m));
        }

        public Task<BalanceRangeDto> GetRangeAsync(Guid merchantId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> RebuildAsync(Guid merchantId, DateOnly fromDate, DateOnly toDate, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeCache : IBalanceCache
    {
        public DailyBalanceDto? Stored { get; set; }

        public long Version { get; set; }

        public int Invalidations { get; private set; }

        public long? SetWithVersion { get; private set; }

        public Task<CacheLookup> LookupAsync(Guid merchantId, DateOnly day, CancellationToken cancellationToken) =>
            Task.FromResult(new CacheLookup(Stored, Version));

        public Task SetAsync(DailyBalanceDto balance, long version, CancellationToken cancellationToken)
        {
            Stored = balance;
            SetWithVersion = version;
            return Task.CompletedTask;
        }

        public Task InvalidateMerchantAsync(Guid merchantId, CancellationToken cancellationToken)
        {
            Invalidations++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Parse_ValidMessage_ReturnsEvent()
    {
        var entryEvent = EntryEvent.Parse(Message("Debit", 25.5m));

        Assert.Equal(Merchant, entryEvent.MerchantId);
        Assert.False(entryEvent.IsCredit);
        Assert.Equal(25.5m, entryEvent.Amount);
        Assert.Equal(Day, entryEvent.EntryDate);
    }

    [Theory]
    [InlineData("Refund", 10, 1)]
    [InlineData("Credit", 0, 1)]
    [InlineData("Credit", -3, 1)]
    [InlineData("Credit", 10, 2)]
    public void Parse_InvalidContent_ThrowsInvalidEvent(string type, double amount, int schemaVersion)
    {
        Assert.Throws<InvalidEventException>(() => EntryEvent.Parse(Message(type, (decimal)amount, schemaVersion)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Parse_Garbage_ThrowsInvalidEvent(string text)
    {
        Assert.Throws<InvalidEventException>(() => EntryEvent.Parse(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public async Task Apply_NewEvent_InvalidatesTheMerchantCache()
    {
        var cache = new FakeCache();
        var applier = new EntryEventApplier(new FakeStore(), cache, TimeProvider.System);

        Assert.True(await applier.HandleAsync(Message(), default));
        Assert.Equal(1, cache.Invalidations);
    }

    [Fact]
    public async Task Apply_DuplicateEvent_DoesNotInvalidateTheCache()
    {
        var cache = new FakeCache();
        var applier = new EntryEventApplier(new FakeStore { Duplicate = true }, cache, TimeProvider.System);

        Assert.False(await applier.HandleAsync(Message(), default));
        Assert.Equal(0, cache.Invalidations);
    }

    [Fact]
    public async Task GetBalance_CacheHit_DoesNotTouchTheDatabase()
    {
        var store = new FakeStore();
        var cache = new FakeCache { Stored = new DailyBalanceDto(Merchant, Day, 1m, 0m, 1m, 1m) };

        var balance = await new GetBalanceHandler(store, cache).HandleAsync(Merchant, Day, default);

        Assert.Equal(1m, balance.Credits);
        Assert.Equal(0, store.DailyReads);
    }

    [Fact]
    public async Task GetBalance_CacheMiss_ReadsDatabaseAndCachesWithTheLookupVersion()
    {
        var store = new FakeStore();
        var cache = new FakeCache { Version = 7 };

        var balance = await new GetBalanceHandler(store, cache).HandleAsync(Merchant, Day, default);

        Assert.Equal(60m, balance.DayResult);
        Assert.Equal(1, store.DailyReads);
        Assert.Equal(7, cache.SetWithVersion);
    }

    [Theory]
    [InlineData("2026-01-10", "2026-01-01")]
    [InlineData("2025-01-01", "2026-12-31")]
    public async Task GetRange_InvalidPeriod_Throws(string from, string to)
    {
        var handler = new GetBalanceRangeHandler(new FakeStore());

        await Assert.ThrowsAsync<InvalidRequestException>(() =>
            handler.HandleAsync(Merchant, DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture), default));
    }

    [Fact]
    public async Task Rebuild_WithoutMerchant_Throws()
    {
        var handler = new RebuildBalancesHandler(new FakeStore(), new FakeCache(), TimeProvider.System);

        await Assert.ThrowsAsync<InvalidRequestException>(() =>
            handler.HandleAsync(new RebuildCommand(Guid.Empty, Day, Day), default));
    }
}
