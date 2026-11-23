namespace CashFlow.Consolidation.Application;

public sealed record DailyBalanceDto(
    Guid MerchantId,
    DateOnly Date,
    decimal Credits,
    decimal Debits,
    decimal DayResult,
    decimal AccumulatedBalance);

public sealed record BalanceRangeDto(
    Guid MerchantId,
    DateOnly From,
    DateOnly To,
    decimal OpeningBalance,
    decimal ClosingBalance,
    IReadOnlyList<DailyBalanceDto> Days);

/// <summary>Persistence of the balance projection and of the received events.</summary>
public interface IBalanceStore
{
    /// <summary>
    /// Records the event and adds it to the daily balance in one transaction.
    /// Returns false when the event was already processed (duplicate delivery): nothing changes.
    /// </summary>
    Task<bool> ApplyAsync(EntryEvent entryEvent, DateTimeOffset receivedAt, CancellationToken cancellationToken);

    Task<DailyBalanceDto> GetDailyAsync(Guid merchantId, DateOnly day, CancellationToken cancellationToken);

    Task<BalanceRangeDto> GetRangeAsync(Guid merchantId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken);

    /// <summary>Recomputes the daily balances of the period from the stored events. Returns the days rebuilt.</summary>
    Task<int> RebuildAsync(Guid merchantId, DateOnly fromDate, DateOnly toDate, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <param name="Balance">The cached value, or null on a miss or when the cache is unavailable.</param>
/// <param name="Version">Version of the merchant data when the lookup ran; -1 when the cache is unavailable.</param>
public sealed record CacheLookup(DailyBalanceDto? Balance, long Version);

/// <summary>
/// Best-effort read cache. Implementations never throw: if the cache is down, the lookup is a miss
/// and the read goes to the database.
/// </summary>
public interface IBalanceCache
{
    Task<CacheLookup> LookupAsync(Guid merchantId, DateOnly day, CancellationToken cancellationToken);

    /// <summary>Stores the value only if the merchant data did not change since <paramref name="version"/>.</summary>
    Task SetAsync(DailyBalanceDto balance, long version, CancellationToken cancellationToken);

    /// <summary>Drops every cached balance of the merchant (an event can change the accumulated balance of later days).</summary>
    Task InvalidateMerchantAsync(Guid merchantId, CancellationToken cancellationToken);
}
