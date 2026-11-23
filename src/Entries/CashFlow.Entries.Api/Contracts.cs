using CashFlow.Entries.Domain;

namespace CashFlow.Entries.Api;

public sealed record RegisterEntryRequest(EntryType Type, decimal Amount, DateOnly EntryDate, string? Description);

public sealed record ReverseEntryRequest(string? Reason);
