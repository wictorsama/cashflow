namespace CashFlow.Consolidation.Application;

/// <summary>Credits and debits of one merchant on one accounting day.</summary>
public sealed record DailyTotal(Guid MerchantId, DateOnly Date, decimal Credits, decimal Debits);

public enum DivergenceKind
{
    /// <summary>The Entries service holds amounts the event log does not (a lost, late or extra event).</summary>
    EntriesVsEvents,

    /// <summary>The daily balance differs from what the event log adds up to (a corrupted projection).</summary>
    EventsVsBalances,
}

/// <summary>"Expected" is the side that is right by definition (Entries, or the event log); "actual" is what is stored.</summary>
public sealed record Divergence(
    DivergenceKind Kind,
    Guid MerchantId,
    DateOnly Date,
    decimal ExpectedCredits,
    decimal ExpectedDebits,
    decimal ActualCredits,
    decimal ActualDebits);

public sealed record ReconciliationReport(
    DateOnly From,
    DateOnly To,
    DateTimeOffset Cutoff,
    int DaysCompared,
    IReadOnlyList<Divergence> Divergences)
{
    public bool IsConsistent => Divergences.Count == 0;
}

/// <summary>The Entries service, the source of truth for what was registered.</summary>
public interface IEntriesTotalsSource
{
    Task<IReadOnlyList<DailyTotal>> GetAsync(
        DateOnly fromDate, DateOnly toDate, DateTimeOffset registeredBefore, CancellationToken cancellationToken);
}

public interface IReconciliationStore
{
    /// <summary>What the event log adds up to, counting only events that occurred up to the cutoff.</summary>
    Task<IReadOnlyList<DailyTotal>> EventLogTotalsAsync(
        DateOnly fromDate, DateOnly toDate, DateTimeOffset occurredBefore, CancellationToken cancellationToken);

    /// <summary>Days where the stored daily balance differs from the event log, read in one consistent snapshot.</summary>
    Task<IReadOnlyList<Divergence>> BalanceDriftAsync(DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken);
}

/// <summary>
/// Checks that the balances never diverge from what was registered (RNF-07), in two steps that point to different
/// causes: Entries against the event log (an event was lost, is late or is extra) and the event log against the stored
/// balances (the projection was corrupted; a rebuild fixes it).
/// </summary>
public sealed class Reconciler(IEntriesTotalsSource entries, IReconciliationStore store, TimeProvider clock)
{
    public const int MaxDays = 31;

    /// <param name="fromDate">First accounting day to compare.</param>
    /// <param name="toDate">Last accounting day to compare.</param>
    /// <param name="grace">Events younger than this are not compared: they may still be on their way through the queue.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<ReconciliationReport> RunAsync(
        DateOnly fromDate, DateOnly toDate, TimeSpan grace, CancellationToken cancellationToken)
    {
        if (toDate < fromDate)
        {
            throw new InvalidRequestException("'to' must not be before 'from'.");
        }

        if (toDate.DayNumber - fromDate.DayNumber + 1 > MaxDays)
        {
            throw new InvalidRequestException($"The period cannot exceed {MaxDays} days.");
        }

        var now = clock.GetUtcNow();
        var cutoff = now - grace;

        var registered = await entries.GetAsync(fromDate, toDate, cutoff, cancellationToken);
        var received = await store.EventLogTotalsAsync(fromDate, toDate, cutoff, cancellationToken);

        var divergences = CompareEntriesWithEvents(registered, received);
        divergences.AddRange(await store.BalanceDriftAsync(fromDate, toDate, cancellationToken));

        var report = new ReconciliationReport(
            fromDate, toDate, cutoff, toDate.DayNumber - fromDate.DayNumber + 1, divergences);
        ConsolidationTelemetry.RecordReconciliation(report, now);

        return report;
    }

    private static List<Divergence> CompareEntriesWithEvents(
        IReadOnlyList<DailyTotal> registered, IReadOnlyList<DailyTotal> received)
    {
        var events = received.ToDictionary(t => (t.MerchantId, t.Date));
        var result = new List<Divergence>();

        // A merchant-day absent on one side counts as zero on that side.
        foreach (var expected in registered)
        {
            events.Remove((expected.MerchantId, expected.Date), out var actual);
            actual ??= new DailyTotal(expected.MerchantId, expected.Date, 0m, 0m);

            if (expected.Credits != actual.Credits || expected.Debits != actual.Debits)
            {
                result.Add(new Divergence(
                    DivergenceKind.EntriesVsEvents, expected.MerchantId, expected.Date,
                    expected.Credits, expected.Debits, actual.Credits, actual.Debits));
            }
        }

        // What is left was received for a merchant-day Entries knows nothing about.
        foreach (var extra in events.Values)
        {
            if (extra.Credits != 0m || extra.Debits != 0m)
            {
                result.Add(new Divergence(
                    DivergenceKind.EntriesVsEvents, extra.MerchantId, extra.Date, 0m, 0m, extra.Credits, extra.Debits));
            }
        }

        return result;
    }
}
