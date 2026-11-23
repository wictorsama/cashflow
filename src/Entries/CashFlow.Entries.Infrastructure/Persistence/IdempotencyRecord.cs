namespace CashFlow.Entries.Infrastructure.Persistence;

/// <summary>
/// The stored answer of a request that carried an Idempotency-Key. Retrying the same key with the same
/// request returns this answer instead of creating the entry again.
/// </summary>
public sealed class IdempotencyRecord
{
    public Guid MerchantId { get; init; }

    public required string Key { get; init; }

    /// <summary>Fingerprint of the request, to detect the same key being reused for a different request.</summary>
    public required string RequestHash { get; init; }

    public int StatusCode { get; init; }

    public required string ResponseBody { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
