using CashFlow.Entries.Domain;

namespace CashFlow.Entries.Application;

public sealed record RegisterEntryCommand(
    Guid MerchantId,
    string CreatedBy,
    EntryType Type,
    decimal Amount,
    DateOnly EntryDate,
    string? Description);

public sealed class RegisterEntryHandler(IEntryRepository entries, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public async Task<EntryDto> HandleAsync(RegisterEntryCommand command, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var entry = Entry.Register(
            command.MerchantId,
            command.Type,
            command.Amount,
            command.EntryDate,
            command.Description,
            command.CreatedBy,
            now,
            MerchantCalendar.Today(now));

        await entries.AddAsync(entry, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return EntryDto.From(entry);
    }
}
