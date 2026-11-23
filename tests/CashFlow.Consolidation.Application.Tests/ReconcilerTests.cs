using CashFlow.Consolidation.Application;

namespace CashFlow.Consolidation.Application.Tests;

public class ReconcilerTests
{
    private static readonly Guid MerchantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MerchantB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateOnly Day = new(2026, 11, 16);
    private static readonly DateOnly OtherDay = new(2026, 11, 15);

    private sealed class FakeEntries(params DailyTotal[] totals) : IEntriesTotalsSource
    {
        public DateTimeOffset? CutoffSeen { get; private set; }

        public Task<IReadOnlyList<DailyTotal>> GetAsync(
            DateOnly fromDate, DateOnly toDate, DateTimeOffset registeredBefore, CancellationToken cancellationToken)
        {
            CutoffSeen = registeredBefore;
            return Task.FromResult<IReadOnlyList<DailyTotal>>(totals);
        }
    }

    private sealed class FakeStore(DailyTotal[] log, Divergence[]? drift = null) : IReconciliationStore
    {
        public Task<IReadOnlyList<DailyTotal>> EventLogTotalsAsync(
            DateOnly fromDate, DateOnly toDate, DateTimeOffset occurredBefore, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DailyTotal>>(log);

        public Task<IReadOnlyList<Divergence>> BalanceDriftAsync(DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Divergence>>(drift ?? []);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);

    private static Task<ReconciliationReport> Run(IEntriesTotalsSource entries, IReconciliationStore store, TimeSpan? grace = null) =>
        new Reconciler(entries, store, new FixedClock(Now)).RunAsync(OtherDay, Day, grace ?? TimeSpan.FromMinutes(2), default);

    [Fact]
    public async Task EverythingMatches_IsConsistent()
    {
        var totals = new[] { new DailyTotal(MerchantA, Day, 100m, 30m), new DailyTotal(MerchantB, OtherDay, 5m, 0m) };

        var report = await Run(new FakeEntries(totals), new FakeStore(totals));

        Assert.True(report.IsConsistent);
        Assert.Equal(2, report.DaysCompared);
    }

    [Fact]
    public async Task EventNeverArrived_IsReportedAsEntriesVsEvents()
    {
        var entries = new FakeEntries(new DailyTotal(MerchantA, Day, 120m, 30m));
        var store = new FakeStore([new DailyTotal(MerchantA, Day, 100m, 30m)]);

        var report = await Run(entries, store);

        var divergence = Assert.Single(report.Divergences);
        Assert.Equal(DivergenceKind.EntriesVsEvents, divergence.Kind);
        Assert.Equal(120m, divergence.ExpectedCredits);
        Assert.Equal(100m, divergence.ActualCredits);
    }

    [Fact]
    public async Task WholeMerchantDayMissing_CountsAsZeroOnThatSide()
    {
        var entries = new FakeEntries(new DailyTotal(MerchantA, Day, 10m, 0m));

        var report = await Run(entries, new FakeStore([]));

        var divergence = Assert.Single(report.Divergences);
        Assert.Equal(10m, divergence.ExpectedCredits);
        Assert.Equal(0m, divergence.ActualCredits);
    }

    [Fact]
    public async Task EventForSomethingEntriesNeverRegistered_IsReported()
    {
        var store = new FakeStore([new DailyTotal(MerchantB, Day, 0m, 42m)]);

        var report = await Run(new FakeEntries(), store);

        var divergence = Assert.Single(report.Divergences);
        Assert.Equal(DivergenceKind.EntriesVsEvents, divergence.Kind);
        Assert.Equal(0m, divergence.ExpectedDebits);
        Assert.Equal(42m, divergence.ActualDebits);
    }

    [Fact]
    public async Task CorruptedProjection_IsReportedAsEventsVsBalances()
    {
        var totals = new[] { new DailyTotal(MerchantA, Day, 100m, 0m) };
        var drift = new Divergence(DivergenceKind.EventsVsBalances, MerchantA, Day, 100m, 0m, 105m, 0m);

        var report = await Run(new FakeEntries(totals), new FakeStore(totals, [drift]));

        Assert.Equal(DivergenceKind.EventsVsBalances, Assert.Single(report.Divergences).Kind);
        Assert.False(report.IsConsistent);
    }

    [Fact]
    public async Task BothKindsAtOnce_AreBothReported()
    {
        var entries = new FakeEntries(new DailyTotal(MerchantA, Day, 100m, 0m));
        var store = new FakeStore([new DailyTotal(MerchantA, Day, 90m, 0m)],
            [new Divergence(DivergenceKind.EventsVsBalances, MerchantB, Day, 1m, 0m, 2m, 0m)]);

        var report = await Run(entries, store);

        Assert.Equal(2, report.Divergences.Count);
        Assert.Contains(report.Divergences, d => d.Kind == DivergenceKind.EntriesVsEvents);
        Assert.Contains(report.Divergences, d => d.Kind == DivergenceKind.EventsVsBalances);
    }

    [Fact]
    public async Task OnlyEntriesOlderThanTheGraceAreCompared()
    {
        var entries = new FakeEntries();

        var report = await Run(entries, new FakeStore([]), grace: TimeSpan.FromMinutes(5));

        Assert.Equal(Now - TimeSpan.FromMinutes(5), entries.CutoffSeen);
        Assert.Equal(report.Cutoff, entries.CutoffSeen);
    }

    [Theory]
    [InlineData("2026-11-16", "2026-11-15")]
    [InlineData("2026-01-01", "2026-12-31")]
    public async Task InvalidPeriod_IsRejected(string from, string to)
    {
        var reconciler = new Reconciler(new FakeEntries(), new FakeStore([]), new FixedClock(Now));

        await Assert.ThrowsAsync<InvalidRequestException>(() => reconciler.RunAsync(
            DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.Zero,
            default));
    }
}
