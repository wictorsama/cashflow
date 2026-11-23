using System.Globalization;
using System.Text.Json;
using CashFlow.Consolidation.Application;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CashFlow.Consolidation.Infrastructure.Caching;

/// <summary>
/// Read cache with a per-merchant version. Cached values live under a key that contains the version; an event
/// bumps the version, which makes every older entry unreachable at once (no need to list the keys to delete).
/// Every Redis failure is swallowed: the cache is an optimization, never a dependency.
/// </summary>
public sealed partial class RedisBalanceCache(IConnectionMultiplexer redis, ILogger<RedisBalanceCache> logger) : IBalanceCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CacheLookup> LookupAsync(Guid merchantId, DateOnly day, CancellationToken cancellationToken)
    {
        try
        {
            var db = redis.GetDatabase();
            var version = await GetVersionAsync(db, merchantId);
            var cached = await db.StringGetAsync(BalanceKey(merchantId, version, day));

            var balance = cached.IsNullOrEmpty ? null : JsonSerializer.Deserialize<DailyBalanceDto>((string)cached!, JsonOptions);

            return new CacheLookup(balance, version);
        }
        catch (Exception ex)
        {
            LogUnavailable(ex);

            return new CacheLookup(null, -1);
        }
    }

    public async Task SetAsync(DailyBalanceDto balance, long version, CancellationToken cancellationToken)
    {
        if (version < 0)
        {
            return;
        }

        try
        {
            var db = redis.GetDatabase();

            // The merchant data may have changed while the database was being read: do not cache a stale value.
            if (await GetVersionAsync(db, balance.MerchantId) != version)
            {
                return;
            }

            await db.StringSetAsync(
                BalanceKey(balance.MerchantId, version, balance.Date), JsonSerializer.Serialize(balance, JsonOptions), Ttl);
        }
        catch (Exception ex)
        {
            LogUnavailable(ex);
        }
    }

    public async Task InvalidateMerchantAsync(Guid merchantId, CancellationToken cancellationToken)
    {
        try
        {
            await redis.GetDatabase().StringIncrementAsync(VersionKey(merchantId));
        }
        catch (Exception ex)
        {
            LogUnavailable(ex);
        }
    }

    private static async Task<long> GetVersionAsync(IDatabase db, Guid merchantId)
    {
        var value = await db.StringGetAsync(VersionKey(merchantId));

        return value.IsNullOrEmpty ? 0 : (long)value;
    }

    private static string VersionKey(Guid merchantId) => $"bal:ver:{merchantId}";

    private static string BalanceKey(Guid merchantId, long version, DateOnly date) =>
        string.Create(CultureInfo.InvariantCulture, $"bal:{merchantId}:{version}:{date:yyyy-MM-dd}");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis is unavailable; serving from the database.")]
    private partial void LogUnavailable(Exception exception);
}
