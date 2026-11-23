using CashFlow.Entries.Domain;

namespace CashFlow.Entries.Application.Tests;

/// <summary>The read side: what is refused before the database is asked, and what is passed through unchanged.</summary>
public class QueryHandlerTests
{
    private static readonly Guid Merchant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateOnly Day = new(2026, 11, 17);

    private sealed class RecordingEntryQueries : IEntryQueries
    {
        public List<(Guid Merchant, DateOnly From, DateOnly To, int Page, int PageSize)> Calls { get; } = [];

        public Task<EntryDto?> GetAsync(Guid merchantId, Guid entryId, CancellationToken cancellationToken) => Task.FromResult<EntryDto?>(null);

        public Task<PagedResult<EntryDto>> ListAsync(Guid merchantId, DateOnly fromDate, DateOnly toDate, int page, int pageSize, CancellationToken cancellationToken)
        {
            Calls.Add((merchantId, fromDate, toDate, page, pageSize));

            return Task.FromResult(new PagedResult<EntryDto>([], page, pageSize, 0));
        }
    }

    private sealed class RecordingTotals : IReconciliationQueries
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<DailyTotal>> GetDailyTotalsAsync(DateOnly fromDate, DateOnly toDate, DateTimeOffset registeredBefore, CancellationToken cancellationToken)
        {
            Calls++;

            return Task.FromResult<IReadOnlyList<DailyTotal>>([new DailyTotal(Merchant, fromDate, 1m, 2m)]);
        }
    }

    // ----- listing entries

    [Fact]
    public async Task List_PassesAValidQueryToTheDatabase_ForTheCallersMerchant()
    {
        var queries = new RecordingEntryQueries();

        var result = await new ListEntriesHandler(queries).HandleAsync(new ListEntriesQuery(Merchant, Day, Day, 2, 50), default);

        Assert.Equal((Merchant, Day, Day, 2, 50), Assert.Single(queries.Calls));
        Assert.Equal(2, result.Page);
    }

    [Theory]
    [InlineData(-1, 0, 1, 50)] // 'to' before 'from'
    [InlineData(0, 366, 1, 50)] // a period over 366 days
    [InlineData(0, 0, 0, 50)] // page below 1
    [InlineData(0, 0, 1, 0)] // page size below 1
    [InlineData(0, 0, 1, 201)] // page size above the maximum
    public async Task List_RefusesAnInvalidQuery_WithoutAskingTheDatabase(int toOffset, int fromOffset, int page, int pageSize)
    {
        var queries = new RecordingEntryQueries();
        var from = Day.AddDays(-fromOffset);
        var to = Day.AddDays(toOffset);

        await Assert.ThrowsAsync<InvalidRequestException>(() =>
            new ListEntriesHandler(queries).HandleAsync(new ListEntriesQuery(Merchant, from, to, page, pageSize), default));

        Assert.Empty(queries.Calls);
    }

    [Fact]
    public async Task List_AcceptsTheLargestPeriodAndPage()
    {
        var queries = new RecordingEntryQueries();

        await new ListEntriesHandler(queries).HandleAsync(
            new ListEntriesQuery(Merchant, Day.AddDays(-(ListEntriesHandler.MaxRangeDays - 1)), Day, 1, ListEntriesHandler.MaxPageSize), default);

        Assert.Single(queries.Calls);
    }

    // ----- totals for the reconciliation job

    [Fact]
    public async Task Totals_AreReadForAValidPeriod()
    {
        var totals = new RecordingTotals();

        var result = await new GetDailyTotalsHandler(totals).HandleAsync(Day, Day, DateTimeOffset.UtcNow, default);

        Assert.Single(result);
        Assert.Equal(1, totals.Calls);
    }

    [Fact]
    public async Task Totals_AcceptTheLargestPeriod()
    {
        var totals = new RecordingTotals();

        await new GetDailyTotalsHandler(totals).HandleAsync(Day.AddDays(-(GetDailyTotalsHandler.MaxDays - 1)), Day, DateTimeOffset.UtcNow, default);

        Assert.Equal(1, totals.Calls);
    }

    [Fact]
    public async Task Totals_RefuseAReversedPeriod_AndAPeriodTooLong()
    {
        var totals = new RecordingTotals();
        var handler = new GetDailyTotalsHandler(totals);

        await Assert.ThrowsAsync<InvalidRequestException>(() => handler.HandleAsync(Day, Day.AddDays(-1), DateTimeOffset.UtcNow, default));
        await Assert.ThrowsAsync<InvalidRequestException>(() => handler.HandleAsync(Day.AddDays(-GetDailyTotalsHandler.MaxDays), Day, DateTimeOffset.UtcNow, default));

        Assert.Equal(0, totals.Calls);
    }

    // ----- what the API returns

    [Fact]
    public void TheDto_CarriesEveryFieldOfTheEntry_IncludingTheAuthor()
    {
        var now = new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);
        var entry = Entry.Register(Merchant, EntryType.Debit, 12.34m, Day, "Rent", "user-9", now, Day);

        var dto = EntryDto.From(entry);

        Assert.Equal((entry.Id, Merchant, EntryType.Debit, 12.34m, Day, "Rent", "user-9", now), (dto.Id, dto.MerchantId, dto.Type, dto.Amount, dto.EntryDate, dto.Description, dto.CreatedBy, dto.RegisteredAt));
        Assert.Null(dto.ReversalOf);
        Assert.Null(dto.ReversedBy);
    }

    [Fact]
    public void TheAuditReport_IsIntactOnlyWithoutIssues()
    {
        var started = new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
        var clean = new AuditReport(true, 1, 3, started, started, 0, []);
        var dirty = new AuditReport(false, 1, 3, started, started, 1, [new AuditIssue(AuditIssueKind.EntryAltered, Merchant, Guid.NewGuid(), 7, "changed")]);

        Assert.True(clean.IsIntact);
        Assert.False(dirty.IsIntact);
        Assert.Equal(AuditIssueKind.EntryAltered, Assert.Single(dirty.Issues).Kind);
    }
}
