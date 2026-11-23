using Microsoft.EntityFrameworkCore;

namespace CashFlow.Consolidation.Infrastructure.Persistence;

/// <summary>
/// Every event received, kept to rebuild balances and to audit them. Its primary key is also the idempotency
/// guard: inserting an event id that already exists means the delivery was a duplicate.
/// </summary>
public sealed class EventLogEntry
{
    public Guid EventId { get; init; }

    public required string EventType { get; init; }

    public Guid MerchantId { get; init; }

    public Guid EntryId { get; init; }

    /// <summary>1 = Debit, 2 = Credit (the same values as the Entries service).</summary>
    public short Type { get; init; }

    public decimal Amount { get; init; }

    public DateOnly EntryDate { get; init; }

    public Guid? OriginalEntryId { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset ReceivedAt { get; init; }
}

/// <summary>Credits and debits of one merchant on one accounting day. The day result is credits minus debits.</summary>
public sealed class DailyBalanceRow
{
    public Guid MerchantId { get; init; }

    public DateOnly Date { get; init; }

    public decimal Credits { get; init; }

    public decimal Debits { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class ConsolidationDbContext(DbContextOptions<ConsolidationDbContext> options) : DbContext(options)
{
    public DbSet<EventLogEntry> EventLog => Set<EventLogEntry>();

    public DbSet<DailyBalanceRow> DailyBalances => Set<DailyBalanceRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EventLogEntry>(log =>
        {
            log.ToTable("event_log");
            log.HasKey(e => e.EventId);
            log.Property(e => e.EventId).HasColumnName("event_id").ValueGeneratedNever();
            log.Property(e => e.EventType).HasColumnName("event_type").HasMaxLength(100);
            log.Property(e => e.MerchantId).HasColumnName("merchant_id");
            log.Property(e => e.EntryId).HasColumnName("entry_id");
            log.Property(e => e.Type).HasColumnName("type");
            log.Property(e => e.Amount).HasColumnName("amount").HasPrecision(18, 2);
            log.Property(e => e.EntryDate).HasColumnName("entry_date");
            log.Property(e => e.OriginalEntryId).HasColumnName("original_entry_id");
            log.Property(e => e.OccurredAt).HasColumnName("occurred_at");
            log.Property(e => e.ReceivedAt).HasColumnName("received_at");

            // Rebuild and reconciliation read the events of a merchant by day.
            log.HasIndex(e => new { e.MerchantId, e.EntryDate });
        });

        modelBuilder.Entity<DailyBalanceRow>(balance =>
        {
            balance.ToTable("daily_balances");
            balance.HasKey(b => new { b.MerchantId, b.Date });
            balance.Property(b => b.MerchantId).HasColumnName("merchant_id");
            balance.Property(b => b.Date).HasColumnName("date");
            balance.Property(b => b.Credits).HasColumnName("credits").HasPrecision(18, 2);
            balance.Property(b => b.Debits).HasColumnName("debits").HasPrecision(18, 2);
            balance.Property(b => b.UpdatedAt).HasColumnName("updated_at");
        });
    }
}

/// <summary>Used only by "dotnet ef" to generate migrations; it never connects to a real database.</summary>
public sealed class DesignTimeDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<ConsolidationDbContext>
{
    public ConsolidationDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ConsolidationDbContext>().UseNpgsql("Host=localhost;Database=consolidation_design").Options);
}
