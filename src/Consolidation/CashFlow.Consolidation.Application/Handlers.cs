namespace CashFlow.Consolidation.Application;

/// <summary>The request is malformed or out of the allowed limits.</summary>
public sealed class InvalidRequestException(string message) : Exception(message);

public sealed class EntryEventApplier(IBalanceStore store, IBalanceCache cache, TimeProvider clock)
{
    /// <returns>True when the event changed the balance; false when it was a duplicate.</returns>
    public async Task<bool> HandleAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        var entryEvent = EntryEvent.Parse(message.Span);

        var now = clock.GetUtcNow();
        var applied = await store.ApplyAsync(entryEvent, now, cancellationToken);
        if (applied)
        {
            // After the commit: a reader that misses the cache from now on reads the new balance.
            await cache.InvalidateMerchantAsync(entryEvent.MerchantId, cancellationToken);
            ConsolidationTelemetry.EventFreshness.Record(Math.Max(0, (clock.GetUtcNow() - entryEvent.OccurredAt).TotalSeconds));
        }

        return applied;
    }
}

public sealed class GetBalanceHandler(IBalanceStore store, IBalanceCache cache)
{
    public async Task<DailyBalanceDto> HandleAsync(Guid merchantId, DateOnly day, CancellationToken cancellationToken)
    {
        var lookup = await cache.LookupAsync(merchantId, day, cancellationToken);
        if (lookup.Balance is not null)
        {
            ConsolidationTelemetry.CacheRequests.Add(1, new KeyValuePair<string, object?>("result", "hit"));

            return lookup.Balance;
        }

        ConsolidationTelemetry.CacheRequests.Add(1, new KeyValuePair<string, object?>("result", lookup.Version < 0 ? "unavailable" : "miss"));

        var balance = await store.GetDailyAsync(merchantId, day, cancellationToken);
        await cache.SetAsync(balance, lookup.Version, cancellationToken);

        return balance;
    }
}

public sealed class GetBalanceRangeHandler(IBalanceStore store)
{
    public const int MaxRangeDays = 366;

    public Task<BalanceRangeDto> HandleAsync(Guid merchantId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken)
    {
        RangeRules.Validate(fromDate, toDate);

        return store.GetRangeAsync(merchantId, fromDate, toDate, cancellationToken);
    }
}

public sealed record RebuildCommand(Guid MerchantId, DateOnly From, DateOnly To);

public sealed class RebuildBalancesHandler(IBalanceStore store, IBalanceCache cache, TimeProvider clock)
{
    public async Task<int> HandleAsync(RebuildCommand command, CancellationToken cancellationToken)
    {
        if (command.MerchantId == Guid.Empty)
        {
            throw new InvalidRequestException("merchantId is required.");
        }

        RangeRules.Validate(command.From, command.To);

        var days = await store.RebuildAsync(command.MerchantId, command.From, command.To, clock.GetUtcNow(), cancellationToken);
        await cache.InvalidateMerchantAsync(command.MerchantId, cancellationToken);

        return days;
    }
}

internal static class RangeRules
{
    public static void Validate(DateOnly fromDate, DateOnly toDate)
    {
        if (toDate < fromDate)
        {
            throw new InvalidRequestException("'to' must not be before 'from'.");
        }

        if (toDate.DayNumber - fromDate.DayNumber + 1 > GetBalanceRangeHandler.MaxRangeDays)
        {
            throw new InvalidRequestException($"The period cannot exceed {GetBalanceRangeHandler.MaxRangeDays} days.");
        }
    }
}
