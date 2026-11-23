using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CashFlow.Consolidation.Application;

/// <summary>Traces and metrics of the Consolidation service (names are the contract with the dashboards).</summary>
public static class ConsolidationTelemetry
{
    public const string Name = "CashFlow.Consolidation";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);
    private static long _queueDepth;
    private static long _deadLetterDepth;
    private static long _entriesVsEvents;
    private static long _eventsVsBalances;
    private static long _lastReconciliation;

    /// <summary>Outcome of each delivery: applied, duplicate, invalid (to the DLQ) or retry (back to the queue).</summary>
    public static readonly Counter<long> EventsProcessed =
        Meter.CreateCounter<long>("cashflow.events.processed", description: "Entry events handled by the consumer, by result.");

    /// <summary>The freshness SLO: how old an event is when its effect reaches the balance.</summary>
    public static readonly Histogram<double> EventFreshness =
        Meter.CreateHistogram<double>("cashflow.event.freshness", unit: "s", description: "Time from the entry to its effect on the balance.");

    public static readonly Counter<long> CacheRequests =
        Meter.CreateCounter<long>("cashflow.cache.requests", description: "Balance reads by cache result: hit, miss or unavailable.");

    /// <summary>Reconciliation runs by result: consistent, diverged or failed.</summary>
    public static readonly Counter<long> ReconciliationRuns =
        Meter.CreateCounter<long>("cashflow.reconciliation.runs", description: "Reconciliation runs, by result.");

    private static readonly ObservableGauge<long> DivergenceGauge = Meter.CreateObservableGauge(
        "cashflow.reconciliation.divergences",
        () => new[]
        {
            new Measurement<long>(Volatile.Read(ref _entriesVsEvents), new KeyValuePair<string, object?>("kind", "entries_vs_events")),
            new Measurement<long>(Volatile.Read(ref _eventsVsBalances), new KeyValuePair<string, object?>("kind", "events_vs_balances")),
        },
        description: "Merchant-days that disagree in the last reconciliation, by kind.");

    private static readonly ObservableGauge<long> LastRunGauge = Meter.CreateObservableGauge(
        "cashflow.reconciliation.last_run_unixtime",
        () => Volatile.Read(ref _lastReconciliation),
        description: "When the last reconciliation finished (Unix seconds); an old value means it stopped running.");

    private static readonly ObservableGauge<long> QueueGauge =
        Meter.CreateObservableGauge("cashflow.queue.depth", () => Volatile.Read(ref _queueDepth), description: "Messages waiting in the consumer queue.");

    private static readonly ObservableGauge<long> DeadLetterGauge =
        Meter.CreateObservableGauge("cashflow.dlq.depth", () => Volatile.Read(ref _deadLetterDepth), description: "Messages in the dead-letter queue.");

    public static void RecordReconciliation(ReconciliationReport report, DateTimeOffset finishedAt)
    {
        Volatile.Write(ref _entriesVsEvents, report.Divergences.Count(d => d.Kind == DivergenceKind.EntriesVsEvents));
        Volatile.Write(ref _eventsVsBalances, report.Divergences.Count(d => d.Kind == DivergenceKind.EventsVsBalances));
        Volatile.Write(ref _lastReconciliation, finishedAt.ToUnixTimeSeconds());
        ReconciliationRuns.Add(1, new KeyValuePair<string, object?>("result", report.IsConsistent ? "consistent" : "diverged"));
    }

    public static void RecordReconciliationFailure() =>
        ReconciliationRuns.Add(1, new KeyValuePair<string, object?>("result", "failed"));

    public static void SetQueueDepths(long queue, long deadLetter)
    {
        Volatile.Write(ref _queueDepth, queue);
        Volatile.Write(ref _deadLetterDepth, deadLetter);
    }

    /// <summary>Keeps the gauges referenced; they are read by the meter, not by code.</summary>
    internal static object[] Gauges => [QueueGauge, DeadLetterGauge, DivergenceGauge, LastRunGauge];
}
