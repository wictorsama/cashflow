using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CashFlow.Entries.Infrastructure;

/// <summary>Traces and metrics of the Entries service (names are the contract with the dashboards).</summary>
public static class EntriesTelemetry
{
    public const string Name = "CashFlow.Entries";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);
    private static long _outboxPending;

    /// <summary>Events written to the outbox, by type. One per business operation that changed the books.</summary>
    public static readonly Counter<long> EventsRecorded =
        Meter.CreateCounter<long>("cashflow.events.recorded", description: "Integration events stored in the outbox.");

    public static readonly Counter<long> OutboxPublished =
        Meter.CreateCounter<long>("cashflow.outbox.published", description: "Outbox rows published to the broker.");

    /// <summary>How long an event waited in the outbox before the broker confirmed it.</summary>
    public static readonly Histogram<double> OutboxPublishDelay =
        Meter.CreateHistogram<double>("cashflow.outbox.publish_delay", unit: "s", description: "Time from the event to the broker confirmation.");

    private static readonly ObservableGauge<long> PendingGauge =
        Meter.CreateObservableGauge("cashflow.outbox.pending", () => Volatile.Read(ref _outboxPending), description: "Outbox rows not yet published.");

    /// <summary>Audit chain verifications, by result: intact, tampered or failed.</summary>
    public static readonly Counter<long> AuditRuns =
        Meter.CreateCounter<long>("cashflow.audit.verifications", description: "Audit chain verifications, by result.");

    private static long _auditIssues;
    private static long _lastAuditVerification;

    private static readonly ObservableGauge<long> AuditIssuesGauge = Meter.CreateObservableGauge(
        "cashflow.audit.chain_issues", () => Volatile.Read(ref _auditIssues), description: "Problems found by the last audit chain verification.");

    private static readonly ObservableGauge<long> AuditLastGauge = Meter.CreateObservableGauge(
        "cashflow.audit.last_verification_unixtime", () => Volatile.Read(ref _lastAuditVerification), description: "When the last verification finished (Unix seconds).");

    public static void RecordAuditVerification(int issues, DateTimeOffset finishedAt)
    {
        Volatile.Write(ref _auditIssues, issues);
        Volatile.Write(ref _lastAuditVerification, finishedAt.ToUnixTimeSeconds());
    }

    public static void SetOutboxPending(long value) => Volatile.Write(ref _outboxPending, value);

    /// <summary>Keeps the gauge alive for the analyzers; it is read by the meter, not by code.</summary>
    internal static object GaugeInstance => (PendingGauge, AuditIssuesGauge, AuditLastGauge);
}
