using CashFlow.Entries.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Entries.Infrastructure.Messaging;

/// <summary>
/// Publishes one batch of pending outbox rows. Rows are locked with FOR UPDATE SKIP LOCKED, so several
/// replicas of the service can run the relay at once without publishing the same row at the same time.
/// Delivery is at least once: a crash after publishing but before commit republishes the row, and the
/// consumer deduplicates by event id.
/// </summary>
public sealed class OutboxProcessor(IDbContextFactory<EntriesDbContext> dbFactory, IEventPublisher publisher, TimeProvider clock)
{
    public async Task<int> ProcessBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var pending = await db.Outbox
            .FromSqlInterpolated($"""
                SELECT * FROM outbox
                WHERE published_at IS NULL
                ORDER BY occurred_at
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(cancellationToken);

        foreach (var message in pending)
        {
            await publisher.PublishAsync(message, cancellationToken);
            message.PublishedAt = clock.GetUtcNow();

            EntriesTelemetry.OutboxPublished.Add(1);
            EntriesTelemetry.OutboxPublishDelay.Record((message.PublishedAt.Value - message.OccurredAt).TotalSeconds);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return pending.Count;
    }

    public async Task<long> CountPendingAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await db.Outbox.LongCountAsync(m => m.PublishedAt == null, cancellationToken);
    }
}
