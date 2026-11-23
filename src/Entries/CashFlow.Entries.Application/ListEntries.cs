namespace CashFlow.Entries.Application;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>Read side of the Entries service. Implementations must not track or modify entities.</summary>
public interface IEntryQueries
{
    Task<EntryDto?> GetAsync(Guid merchantId, Guid entryId, CancellationToken cancellationToken);

    Task<PagedResult<EntryDto>> ListAsync(
        Guid merchantId, DateOnly fromDate, DateOnly toDate, int page, int pageSize, CancellationToken cancellationToken);
}

public sealed record ListEntriesQuery(Guid MerchantId, DateOnly From, DateOnly To, int Page, int PageSize);

public sealed class ListEntriesHandler(IEntryQueries queries)
{
    public const int MaxRangeDays = 366;
    public const int MaxPageSize = 200;

    public Task<PagedResult<EntryDto>> HandleAsync(ListEntriesQuery query, CancellationToken cancellationToken)
    {
        if (query.To < query.From)
        {
            throw new InvalidRequestException("'to' must not be before 'from'.");
        }

        if (query.To.DayNumber - query.From.DayNumber + 1 > MaxRangeDays)
        {
            throw new InvalidRequestException($"The period cannot exceed {MaxRangeDays} days.");
        }

        if (query.Page < 1)
        {
            throw new InvalidRequestException("'page' must be 1 or greater.");
        }

        if (query.PageSize is < 1 or > MaxPageSize)
        {
            throw new InvalidRequestException($"'pageSize' must be between 1 and {MaxPageSize}.");
        }

        return queries.ListAsync(query.MerchantId, query.From, query.To, query.Page, query.PageSize, cancellationToken);
    }
}
