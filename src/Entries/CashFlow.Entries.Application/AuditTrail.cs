namespace CashFlow.Entries.Application;

public enum AuditIssueKind
{
    /// <summary>A link does not follow the previous one, or its content no longer matches its hash: it was changed, removed or inserted.</summary>
    ChainBroken,

    /// <summary>An entry exists that the chain never recorded (inserted straight into the table).</summary>
    EntryWithoutAudit,

    /// <summary>The chain recorded an entry that is gone (deleted straight from the table).</summary>
    AuditWithoutEntry,

    /// <summary>An entry no longer says what the chain recorded when it was registered (edited straight in the table).</summary>
    EntryAltered,
}

public sealed record AuditIssue(AuditIssueKind Kind, Guid MerchantId, Guid? EntryId, long? Seq, string Detail);

public sealed record AuditReport(
    bool IsIntact, int Merchants, long Links, DateTimeOffset AuditStartedAt, DateTimeOffset VerifiedAt, int IssueCount, IReadOnlyList<AuditIssue> Issues);

/// <summary>Checks that the books still say what the audit chain recorded as they were written.</summary>
public interface IAuditTrailVerifier
{
    Task<AuditReport> VerifyAsync(CancellationToken cancellationToken);
}
