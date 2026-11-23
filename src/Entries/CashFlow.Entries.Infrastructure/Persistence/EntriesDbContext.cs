using CashFlow.Entries.Domain;
using Microsoft.EntityFrameworkCore;

namespace CashFlow.Entries.Infrastructure.Persistence;

public sealed class EntriesDbContext(DbContextOptions<EntriesDbContext> options) : DbContext(options)
{
    public DbSet<Entry> Entries => Set<Entry>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<AuditRecord> AuditLog => Set<AuditRecord>();

    public DbSet<IdempotencyRecord> IdempotencyKeys => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Entry>(entry =>
        {
            entry.ToTable("entries");
            entry.HasKey(e => e.Id);
            entry.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            entry.Property(e => e.MerchantId).HasColumnName("merchant_id");
            entry.Property(e => e.Type).HasColumnName("type").HasConversion<short>();
            entry.Property(e => e.Amount).HasColumnName("amount").HasPrecision(18, 2);
            entry.Property(e => e.EntryDate).HasColumnName("entry_date");
            entry.Property(e => e.Description).HasColumnName("description").HasMaxLength(Domain.Entry.MaxDescriptionLength);
            entry.Property(e => e.CreatedBy).HasColumnName("created_by").HasMaxLength(Domain.Entry.MaxCreatedByLength);
            entry.Property(e => e.RegisteredAt).HasColumnName("registered_at");
            entry.Property(e => e.ReversalOf).HasColumnName("reversal_of");
            entry.Property(e => e.ReversedBy).HasColumnName("reversed_by");
            entry.Ignore(e => e.IsReversal);
            entry.Ignore(e => e.DomainEvents);

            // The listing and the daily balance both filter by merchant and day.
            entry.HasIndex(e => new { e.MerchantId, e.EntryDate });

            // An entry can be reversed only once: concurrent reversals collide here instead of both succeeding.
            entry.HasIndex(e => e.ReversalOf).IsUnique().HasFilter("reversal_of IS NOT NULL");
        });

        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox");
            outbox.HasKey(m => m.Id);
            outbox.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            outbox.Property(m => m.Type).HasColumnName("type").HasMaxLength(100);
            outbox.Property(m => m.Payload).HasColumnName("payload").HasColumnType("jsonb");
            outbox.Property(m => m.OccurredAt).HasColumnName("occurred_at");
            outbox.Property(m => m.PublishedAt).HasColumnName("published_at");
            outbox.Property(m => m.TraceParent).HasColumnName("trace_parent").HasMaxLength(100);

            // The relay only reads unpublished rows, oldest first.
            outbox.HasIndex(m => m.OccurredAt).HasFilter("published_at IS NULL").HasDatabaseName("ix_outbox_pending");
        });

        modelBuilder.Entity<AuditRecord>(audit =>
        {
            audit.ToTable("audit_log");
            audit.HasKey(a => a.Seq);
            audit.Property(a => a.Seq).HasColumnName("seq").ValueGeneratedOnAdd();
            audit.Property(a => a.MerchantId).HasColumnName("merchant_id");
            audit.Property(a => a.EntryId).HasColumnName("entry_id");
            audit.Property(a => a.EventId).HasColumnName("event_id");
            audit.Property(a => a.Action).HasColumnName("action").HasMaxLength(50);
            audit.Property(a => a.Actor).HasColumnName("actor").HasMaxLength(Domain.Entry.MaxCreatedByLength);
            audit.Property(a => a.OccurredAt).HasColumnName("occurred_at");
            audit.Property(a => a.EntryType).HasColumnName("entry_type").HasConversion<short>();
            audit.Property(a => a.Amount).HasColumnName("amount").HasPrecision(18, 2);
            audit.Property(a => a.EntryDate).HasColumnName("entry_date");
            audit.Property(a => a.OriginalEntryId).HasColumnName("original_entry_id");
            audit.Property(a => a.PrevHash).HasColumnName("prev_hash").HasMaxLength(64).IsFixedLength();
            audit.Property(a => a.Hash).HasColumnName("hash").HasMaxLength(64).IsFixedLength();

            // A chain is read in order, one merchant at a time; one link per entry.
            audit.HasIndex(a => new { a.MerchantId, a.Seq });
            audit.HasIndex(a => a.EntryId).IsUnique();
        });

        modelBuilder.Entity<IdempotencyRecord>(record =>
        {
            record.ToTable("idempotency_keys");
            record.HasKey(r => new { r.MerchantId, r.Key });
            record.Property(r => r.MerchantId).HasColumnName("merchant_id");
            record.Property(r => r.Key).HasColumnName("key").HasMaxLength(100);
            record.Property(r => r.RequestHash).HasColumnName("request_hash").HasMaxLength(64);
            record.Property(r => r.StatusCode).HasColumnName("status_code");
            record.Property(r => r.ResponseBody).HasColumnName("response_body").HasColumnType("jsonb");
            record.Property(r => r.CreatedAt).HasColumnName("created_at");

            // Lets a periodic cleanup delete old keys efficiently.
            record.HasIndex(r => r.CreatedAt);
        });
    }
}
