namespace CashFlow.Assistant.Api;

/// <summary>The sentence the merchant typed. Nothing else is accepted: no merchant id, no history.</summary>
public sealed record DraftRequest(string? Text);
