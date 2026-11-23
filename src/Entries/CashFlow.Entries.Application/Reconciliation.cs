namespace CashFlow.Entries.Application;

/// <summary>What the Entries service holds for one merchant on one accounting day. Reversals count as entries.</summary>
public sealed record DailyTotal(Guid MerchantId, DateOnly Date, decimal Credits, decimal Debits);

/// <summary>Cross-merchant read used only by the reconciliation job, never by a merchant.</summary>
public interface IReconciliationQueries
{
    Task<IReadOnlyList<DailyTotal>> GetDailyTotalsAsync(
        DateOnly fromDate, DateOnly toDate, DateTimeOffset registeredBefore, CancellationToken cancellationToken);
}

public sealed class GetDailyTotalsHandler(IReconciliationQueries queries)
{
    public const int MaxDays = 31;

    public Task<IReadOnlyList<DailyTotal>> HandleAsync(
        DateOnly fromDate, DateOnly toDate, DateTimeOffset registeredBefore, CancellationToken cancellationToken)
    {
        if (toDate < fromDate)
        {
            throw new InvalidRequestException("'to' must not be before 'from'.");
        }

        if (toDate.DayNumber - fromDate.DayNumber + 1 > MaxDays)
        {
            throw new InvalidRequestException($"The period cannot exceed {MaxDays} days.");
        }

        return queries.GetDailyTotalsAsync(fromDate, toDate, registeredBefore, cancellationToken);
    }
}
