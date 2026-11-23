namespace CashFlow.Entries.Domain;

/// <summary>Raised when a business invariant of the Entries domain is violated.</summary>
public class DomainException(string message) : Exception(message);

/// <summary>The entry was already reversed. Callers map it to a conflict, not to a validation error.</summary>
public sealed class EntryAlreadyReversedException(Guid entryId)
    : DomainException($"Entry {entryId} was already reversed.");
