namespace CashFlow.Entries.Application;

public sealed class EntryNotFoundException(Guid entryId)
    : Exception($"Entry {entryId} was not found.")
{
    public Guid EntryId { get; } = entryId;
}
