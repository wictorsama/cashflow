namespace CashFlow.Assistant.Core;

/// <summary>What an interpreter says it understood. It is UNTRUSTED: a model can be wrong or manipulated.</summary>
/// <param name="Outcome">"draft" (understood), "clarify" (something is missing or ambiguous) or "reject" (not a cash flow entry).</param>
public sealed record RawInterpretation(
    string Outcome,
    string? Type,
    decimal? Amount,
    string? Date,
    string? Description,
    string? Question,
    int InputTokens = 0,
    int OutputTokens = 0)
{
    public const string Draft = "draft";
    public const string Clarify = "clarify";
    public const string Reject = "reject";

    public static RawInterpretation Ask(string question) => new(Clarify, null, null, null, null, question);

    public static RawInterpretation Refuse() => new(Reject, null, null, null, null, null);
}

/// <summary>A draft that passed the same rules the entry itself must pass. The user still has to confirm it.</summary>
public sealed record EntryDraft(string Type, decimal Amount, DateOnly EntryDate, string? Description);

public enum DraftStatus
{
    /// <summary>A proposal for the user to review and confirm. Nothing was recorded.</summary>
    Draft,

    /// <summary>The text is ambiguous or incomplete: the assistant asks instead of guessing.</summary>
    NeedsClarification,

    /// <summary>The text is not a description of a cash flow entry, or what came back was not usable.</summary>
    Rejected,
}

public sealed record DraftResult(DraftStatus Status, EntryDraft? Draft, string? Question, string? Reason, string Provider)
{
    public static DraftResult Proposal(EntryDraft draft, string provider) => new(DraftStatus.Draft, draft, null, null, provider);

    public static DraftResult Ask(string question, string provider) => new(DraftStatus.NeedsClarification, null, question, null, provider);

    public static DraftResult Reject(string reason, string provider) => new(DraftStatus.Rejected, null, null, reason, provider);
}

/// <summary>Turns a sentence into a raw interpretation. Implementations: the real model, a local model, or a deterministic one.</summary>
public interface IEntryInterpreter
{
    string Name { get; }

    Task<RawInterpretation> InterpretAsync(string text, DateOnly today, CancellationToken cancellationToken);
}

/// <summary>The provider could not answer (down, slow, refused). The caller falls back to the ordinary form.</summary>
public sealed class AssistantUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
