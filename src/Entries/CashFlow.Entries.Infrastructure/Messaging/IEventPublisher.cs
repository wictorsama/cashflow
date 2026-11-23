using CashFlow.Entries.Infrastructure.Persistence;

namespace CashFlow.Entries.Infrastructure.Messaging;

public interface IEventPublisher
{
    /// <summary>Returns only after the broker confirmed it took responsibility for the message.</summary>
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}
