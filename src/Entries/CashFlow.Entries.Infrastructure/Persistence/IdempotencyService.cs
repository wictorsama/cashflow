using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CashFlow.Entries.Infrastructure.Persistence;

/// <summary>The key was already used for a different request.</summary>
public sealed class IdempotencyKeyReusedException()
    : Exception("The Idempotency-Key was already used with a different request.");

public sealed record IdempotentResponse(int StatusCode, string Body, bool Replayed);

/// <summary>
/// Runs an operation at most once per (merchant, key). The stored answer is committed in the same transaction
/// as the operation, so a crash cannot leave an entry without its key, or a key without its entry.
/// </summary>
public sealed class IdempotencyService(EntriesDbContext db, TimeProvider clock)
{
    private const string PrimaryKeyConstraint = "PK_idempotency_keys";

    public async Task<IdempotentResponse> ExecuteAsync(
        Guid merchantId,
        string key,
        string requestHash,
        Func<Task<(int StatusCode, string Body)>> operation,
        CancellationToken cancellationToken)
    {
        var stored = await FindAsync(merchantId, key, cancellationToken);
        if (stored is not null)
        {
            return Replay(stored, requestHash);
        }

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var (statusCode, body) = await operation();

            db.IdempotencyKeys.Add(new IdempotencyRecord
            {
                MerchantId = merchantId,
                Key = key,
                RequestHash = requestHash,
                StatusCode = statusCode,
                ResponseBody = body,
                CreatedAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new IdempotentResponse(statusCode, body, Replayed: false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { ConstraintName: PrimaryKeyConstraint })
        {
            // Two requests with the same key ran at once; the other one committed first. Its answer wins.
            db.ChangeTracker.Clear();
            var winner = await FindAsync(merchantId, key, cancellationToken)
                ?? throw new InvalidOperationException("Idempotency record vanished after a key conflict.");

            return Replay(winner, requestHash);
        }
    }

    private Task<IdempotencyRecord?> FindAsync(Guid merchantId, string key, CancellationToken cancellationToken) =>
        db.IdempotencyKeys.AsNoTracking()
            .FirstOrDefaultAsync(r => r.MerchantId == merchantId && r.Key == key, cancellationToken);

    private static IdempotentResponse Replay(IdempotencyRecord stored, string requestHash) =>
        stored.RequestHash == requestHash
            ? new IdempotentResponse(stored.StatusCode, stored.ResponseBody, Replayed: true)
            : throw new IdempotencyKeyReusedException();
}
