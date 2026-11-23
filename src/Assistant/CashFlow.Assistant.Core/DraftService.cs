using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace CashFlow.Assistant.Core;

public static class AssistantTelemetry
{
    public const string Name = "CashFlow.Assistant";

    public static readonly ActivitySource Source = new(Name);

    private static readonly Meter Meter = new(Name);

    public static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "cashflow.assistant.requests", description: "Draft requests, by provider and result (draft, clarify, rejected, unavailable, invalid_input).");

    public static readonly Histogram<double> Latency = Meter.CreateHistogram<double>(
        "cashflow.assistant.latency", unit: "s", description: "Time to produce a draft, provider call included.");

    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>(
        "cashflow.assistant.tokens", description: "Tokens sent to and received from the provider; the basis of the cost estimate.");
}

/// <summary>
/// The only thing the assistant does: turn a sentence into a validated draft. It has no way to record an entry (it holds
/// no credentials for the Entries service and calls nothing but its provider), and it stores nothing: the sentence lives
/// in memory for the length of one request and is never logged.
/// </summary>
public sealed partial class DraftService(IEntryInterpreter interpreter, TimeProvider clock, ILogger<DraftService> logger)
{
    public string Provider => interpreter.Name;

    public async Task<DraftResult> DraftAsync(string? text, DateOnly today, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var provider = interpreter.Name;

        var cleaned = DraftValidator.CleanInput(text);
        if (cleaned is null)
        {
            Count(provider, "invalid_input", started);

            return DraftResult.Reject($"Escreva de 1 a {DraftValidator.MaxInputLength} caracteres descrevendo o lançamento.", provider);
        }

        using var activity = AssistantTelemetry.Source.StartActivity("assistant.draft");
        activity?.SetTag("assistant.provider", provider);

        try
        {
            var raw = await interpreter.InterpretAsync(cleaned, today, cancellationToken);
            AssistantTelemetry.Tokens.Add(raw.InputTokens, new KeyValuePair<string, object?>("direction", "input"), new KeyValuePair<string, object?>("provider", provider));
            AssistantTelemetry.Tokens.Add(raw.OutputTokens, new KeyValuePair<string, object?>("direction", "output"), new KeyValuePair<string, object?>("provider", provider));

            var result = DraftValidator.Validate(raw, today, provider);
            Count(provider, ResultName(result.Status), started);
            if (raw.Outcome == RawInterpretation.Draft && result.Status != DraftStatus.Draft)
            {
                // The model said "draft" but the rules refused it: worth watching (a drifting prompt, or manipulation).
                LogDraftRefused(provider);
            }

            return result;
        }
        catch (AssistantUnavailableException e)
        {
            Count(provider, "unavailable", started);
            LogUnavailable(provider, e.Message);

            throw;
        }
    }

    public DateOnly Today() => TodayIn(clock.GetUtcNow());

    public static DateOnly TodayIn(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")).DateTime);

    private static string ResultName(DraftStatus status) => status switch
    {
        DraftStatus.Draft => "draft",
        DraftStatus.NeedsClarification => "clarify",
        _ => "rejected",
    };

    private static void Count(string provider, string result, long started)
    {
        AssistantTelemetry.Requests.Add(1, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("result", result));
        AssistantTelemetry.Latency.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("provider", provider));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider {Provider} proposed a draft that the validation refused.")]
    private partial void LogDraftRefused(string provider);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Provider {Provider} is unavailable: {Reason}")]
    private partial void LogUnavailable(string provider, string reason);
}
