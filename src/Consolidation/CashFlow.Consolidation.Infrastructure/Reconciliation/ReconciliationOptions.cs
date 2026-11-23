namespace CashFlow.Consolidation.Infrastructure.Reconciliation;

public sealed class ReconciliationOptions
{
    /// <summary>The periodic job is off unless enabled; the on-demand endpoint works whenever the source is configured.</summary>
    public bool Enabled { get; init; }

    /// <summary>Base address of the Entries service, reached from inside the cluster.</summary>
    public string EntriesUrl { get; init; } = string.Empty;

    /// <summary>Keycloak token endpoint (client credentials) the job uses to read the Entries totals.</summary>
    public string TokenUrl { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    public string ClientSecret { get; init; } = string.Empty;

    public int IntervalMinutes { get; init; } = 10;

    /// <summary>How many accounting days back from today are compared.</summary>
    public int WindowDays { get; init; } = 3;

    /// <summary>Events younger than this are not compared: they may still be on their way through the queue.</summary>
    public int GraceSeconds { get; init; } = 120;

    public bool CanReadEntries =>
        !string.IsNullOrWhiteSpace(EntriesUrl) && !string.IsNullOrWhiteSpace(TokenUrl) && !string.IsNullOrWhiteSpace(ClientId);
}
