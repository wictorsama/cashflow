using CashFlow.Entries.Application;
using CashFlow.Entries.Domain;
using CashFlow.Entries.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CashFlow.Entries.Integration.Tests;

/// <summary>
/// The audit chain makes a change to the books outside the service visible. Real PostgreSQL: the trigger that keeps the
/// log append-only, the per-merchant lock and the verification are all exercised for real.
/// </summary>
public sealed class AuditTrailTests(InfrastructureFixture infra) : IClassFixture<InfrastructureFixture>
{
    private static readonly Guid MerchantA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MerchantB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static (RegisterEntryHandler Register, ReverseEntryHandler Reverse) Handlers(EntriesDbContext db)
    {
        var repository = new EntryRepository(db);
        var unitOfWork = new EfUnitOfWork(db);

        return (new RegisterEntryHandler(repository, unitOfWork, TimeProvider.System), new ReverseEntryHandler(repository, unitOfWork, TimeProvider.System));
    }

    private static RegisterEntryCommand Entry(Guid merchant, decimal amount = 10m, string actor = "user-1") =>
        new(merchant, actor, EntryType.Credit, amount, MerchantCalendar.Today(DateTimeOffset.UtcNow), "Sale");

    private static async Task<AuditReport> VerifyAsync(string connection)
    {
        await using var db = InfrastructureFixture.NewContext(connection);

        return await new AuditTrailVerifier(db, TimeProvider.System).VerifyAsync(default);
    }

    /// <summary>What a person with full access to the database could do: switch the protection off and edit.</summary>
    private static async Task TamperAsync(string connection, FormattableString sql)
    {
        await using var db = InfrastructureFixture.NewContext(connection);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE audit_log DISABLE TRIGGER USER");
        await db.Database.ExecuteSqlInterpolatedAsync(sql);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE audit_log ENABLE TRIGGER USER");
    }

    // ----- how the chain is built

    [Fact]
    public async Task EveryEntryAndEveryReversal_AddsALink_ThatPointsAtThePreviousOne()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var db = InfrastructureFixture.NewContext(connection);
        var handlers = Handlers(db);
        var first = await handlers.Register.HandleAsync(Entry(MerchantA, 10m), default);
        await handlers.Register.HandleAsync(Entry(MerchantA, 20m), default);
        await handlers.Reverse.HandleAsync(new ReverseEntryCommand(MerchantA, "user-2", first.Id, "wrong"), default);

        await using var check = InfrastructureFixture.NewContext(connection);
        var links = await check.AuditLog.AsNoTracking().OrderBy(a => a.Seq).ToListAsync();

        Assert.Equal(3, links.Count);
        Assert.Equal(AuditChain.Genesis, links[0].PrevHash);
        Assert.Equal(links[0].Hash, links[1].PrevHash);
        Assert.Equal(links[1].Hash, links[2].PrevHash);
        Assert.All(links, l => Assert.Equal(AuditChain.HashOf(l), l.Hash));
        Assert.Equal(["EntryRegistered", "EntryRegistered", "EntryReversed"], links.Select(l => l.Action));
        Assert.Equal("user-2", links[2].Actor);

        var report = await VerifyAsync(connection);
        Assert.True(report.IsIntact);
        Assert.Equal(3, report.Links);
    }

    [Fact]
    public async Task EachMerchant_HasItsOwnChain()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var db = InfrastructureFixture.NewContext(connection);
        var handlers = Handlers(db);
        await handlers.Register.HandleAsync(Entry(MerchantA), default);
        await handlers.Register.HandleAsync(Entry(MerchantB), default);

        await using var check = InfrastructureFixture.NewContext(connection);
        var links = await check.AuditLog.AsNoTracking().ToListAsync();

        Assert.All(links, l => Assert.Equal(AuditChain.Genesis, l.PrevHash)); // neither follows the other
        var report = await VerifyAsync(connection);
        Assert.True(report.IsIntact);
        Assert.Equal(2, report.Merchants);
    }

    [Fact]
    public async Task ConcurrentWritesOfTheSameMerchant_TakeTurns_AndFormOneUnbrokenChain()
    {
        var connection = await infra.CreateDatabaseAsync();

        await Task.WhenAll(Enumerable.Range(1, 25).Select(async i =>
        {
            await using var db = InfrastructureFixture.NewContext(connection); // each request has its own context
            await Handlers(db).Register.HandleAsync(Entry(MerchantA, i), default);
        }));

        var report = await VerifyAsync(connection);
        Assert.True(report.IsIntact, string.Join("; ", report.Issues.Select(i => i.Detail)));
        Assert.Equal(25, report.Links);
        await using var check = InfrastructureFixture.NewContext(connection);
        Assert.Equal(25, await check.AuditLog.Select(a => a.PrevHash).Distinct().CountAsync()); // no two links share a parent
    }

    [Fact]
    public async Task AWriteThatFails_LeavesNoLinkBehind()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var db = InfrastructureFixture.NewContext(connection);
        var handlers = Handlers(db);
        var created = await handlers.Register.HandleAsync(Entry(MerchantA), default);
        await handlers.Reverse.HandleAsync(new ReverseEntryCommand(MerchantA, "u", created.Id, null), default);

        await using var second = InfrastructureFixture.NewContext(connection);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            Handlers(second).Reverse.HandleAsync(new ReverseEntryCommand(MerchantA, "u", created.Id, null), default)); // already reversed

        Assert.Equal(2, (await VerifyAsync(connection)).Links);
    }

    [Fact]
    public async Task AnEmptyChain_IsIntact()
    {
        var report = await VerifyAsync(await infra.CreateDatabaseAsync());

        Assert.True(report.IsIntact);
        Assert.Equal(0, report.Links);
    }

    // ----- the log only grows

    [Fact]
    public async Task TheLog_RefusesUpdateDeleteAndTruncate()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var db = InfrastructureFixture.NewContext(connection);
        await Handlers(db).Register.HandleAsync(Entry(MerchantA), default);

        foreach (var statement in new[] { "UPDATE audit_log SET actor = 'someone-else'", "DELETE FROM audit_log", "TRUNCATE audit_log" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(statement));
            Assert.Equal("23001", error.SqlState); // restrict_violation
        }

        Assert.Equal(1, (await VerifyAsync(connection)).Links);
    }

    // ----- changes made outside the service are found

    [Fact]
    public async Task AnEntryEditedInTheTable_IsFound()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var db = InfrastructureFixture.NewContext(connection);
        var created = await Handlers(db).Register.HandleAsync(Entry(MerchantA, 100m), default);

        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE entries SET amount = 1 WHERE id = {created.Id}");

        var report = await VerifyAsync(connection);
        Assert.False(report.IsIntact);
        var issue = Assert.Single(report.Issues);
        Assert.Equal(AuditIssueKind.EntryAltered, issue.Kind);
        Assert.Equal(created.Id, issue.EntryId);
    }

    [Fact]
    public async Task AnEntryDeletedFromTheTable_IsFound()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var db = InfrastructureFixture.NewContext(connection);
        var created = await Handlers(db).Register.HandleAsync(Entry(MerchantA), default);

        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM entries WHERE id = {created.Id}");

        var report = await VerifyAsync(connection);
        Assert.Contains(report.Issues, i => i.Kind == AuditIssueKind.AuditWithoutEntry && i.EntryId == created.Id);
    }

    [Fact]
    public async Task AnEntryInsertedStraightIntoTheTable_IsFound()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var db = InfrastructureFixture.NewContext(connection);
        var forged = Domain.Entry.Register(MerchantA, EntryType.Credit, 999m, MerchantCalendar.Today(DateTimeOffset.UtcNow), "forged", "intruder", DateTimeOffset.UtcNow, MerchantCalendar.Today(DateTimeOffset.UtcNow));
        await db.Entries.AddAsync(forged);
        await db.SaveChangesAsync(); // bypasses the unit of work: no link

        var report = await VerifyAsync(connection);
        Assert.Contains(report.Issues, i => i.Kind == AuditIssueKind.EntryWithoutAudit && i.EntryId == forged.Id);
    }

    [Fact]
    public async Task ALinkEditedAfterTheFact_BreaksTheChain()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var db = InfrastructureFixture.NewContext(connection);
        await Handlers(db).Register.HandleAsync(Entry(MerchantA, 10m, "honest"), default);
        await Handlers(db).Register.HandleAsync(Entry(MerchantA, 20m), default);

        await TamperAsync(connection, $"UPDATE audit_log SET actor = 'someone-else' WHERE seq = (SELECT min(seq) FROM audit_log)");

        var report = await VerifyAsync(connection);
        Assert.Contains(report.Issues, i => i.Kind == AuditIssueKind.ChainBroken && i.Detail.Contains("changed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALinkRemovedFromTheMiddle_BreaksTheChain()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var db = InfrastructureFixture.NewContext(connection);
        foreach (var amount in new[] { 1m, 2m, 3m })
        {
            await Handlers(db).Register.HandleAsync(Entry(MerchantA, amount), default);
        }

        await TamperAsync(connection, $"DELETE FROM audit_log WHERE seq = (SELECT seq FROM audit_log ORDER BY seq OFFSET 1 LIMIT 1)");

        var report = await VerifyAsync(connection);
        Assert.Contains(report.Issues, i => i.Kind == AuditIssueKind.ChainBroken && i.Detail.Contains("does not follow", StringComparison.Ordinal));
        Assert.Contains(report.Issues, i => i.Kind == AuditIssueKind.EntryWithoutAudit); // and its entry has no link any more
    }

    [Fact]
    public async Task ATamperedChain_IsReported_WithAnExactCount_AndAtMostTheListedIssues()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var db = InfrastructureFixture.NewContext(connection);
        await Handlers(db).Register.HandleAsync(Entry(MerchantA, 5m), default);
        await db.Database.ExecuteSqlRawAsync("UPDATE entries SET amount = amount + 1");

        var report = await VerifyAsync(connection);

        Assert.Equal(report.Issues.Count, report.IssueCount);
        Assert.True(report.IssueCount > 0);
    }

    [Fact]
    public async Task EntriesFromBeforeTheChainExisted_AreNotExpectedToBeInIt()
    {
        var connection = await infra.CreateDatabaseAsync();
        await using var db = InfrastructureFixture.NewContext(connection);
        await Handlers(db).Register.HandleAsync(Entry(MerchantA), default);

        // As if the entry had been registered before the chain started, then its link is lost with nothing to compare.
        await db.Database.ExecuteSqlRawAsync("UPDATE audit_meta SET value = to_char((now() + interval '1 hour') AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') WHERE key = 'started_at'");
        await TamperAsync(connection, $"DELETE FROM audit_log");

        Assert.True((await VerifyAsync(connection)).IsIntact);
    }

    // ----- the hash

    [Fact]
    public void TheHash_ChangesWithEveryFieldItCovers()
    {
        var baseLink = Link();
        var baseline = AuditChain.HashOf(baseLink);

        Assert.Equal(64, baseline.Length);
        Assert.Equal(baseline, AuditChain.HashOf(Link())); // deterministic
        Assert.NotEqual(baseline, AuditChain.HashOf(Link(amount: 10.01m)));
        Assert.NotEqual(baseline, AuditChain.HashOf(Link(actor: "other")));
        Assert.NotEqual(baseline, AuditChain.HashOf(Link(merchant: MerchantB)));
        Assert.NotEqual(baseline, AuditChain.HashOf(Link(prev: new string('1', 64))));
        Assert.NotEqual(baseline, AuditChain.HashOf(Link(action: "EntryReversed")));
        Assert.NotEqual(baseline, AuditChain.HashOf(Link(type: EntryType.Debit)));
        Assert.NotEqual(baseline, AuditChain.HashOf(Link(at: new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).AddTicks(10))));
    }

    [Fact]
    public void Truncate_KeepsMicroseconds_SoTheHashSurvivesTheDatabase()
    {
        var value = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);

        Assert.Equal(1234560, AuditChain.Truncate(value).UtcTicks - new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero).UtcTicks);
    }

    private static AuditRecord Link(
        decimal amount = 10m, string actor = "user-1", Guid? merchant = null, string? prev = null, string action = "EntryRegistered", EntryType type = EntryType.Credit, DateTimeOffset? at = null) =>
        new()
        {
            MerchantId = merchant ?? MerchantA,
            EntryId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            EventId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Action = action,
            Actor = actor,
            OccurredAt = at ?? new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            EntryType = type,
            Amount = amount,
            EntryDate = new DateOnly(2026, 11, 20),
            PrevHash = prev ?? AuditChain.Genesis,
            Hash = string.Empty,
        };
}
