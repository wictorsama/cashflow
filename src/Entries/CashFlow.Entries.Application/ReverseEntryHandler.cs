namespace CashFlow.Entries.Application;

public sealed record ReverseEntryCommand(Guid MerchantId, string CreatedBy, Guid EntryId, string? Reason);

public sealed class ReverseEntryHandler(IEntryRepository entries, IUnitOfWork unitOfWork, TimeProvider clock)
{
    /// <summary>Returns the compensating entry. The original is marked as reversed in the same transaction.</summary>
    public async Task<EntryDto> HandleAsync(ReverseEntryCommand command, CancellationToken cancellationToken)
    {
        var original = await entries.FindAsync(command.MerchantId, command.EntryId, cancellationToken)
            ?? throw new EntryNotFoundException(command.EntryId);

        var reversal = original.Reverse(command.Reason, command.CreatedBy, clock.GetUtcNow());

        await entries.AddAsync(reversal, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return EntryDto.From(reversal);
    }
}
