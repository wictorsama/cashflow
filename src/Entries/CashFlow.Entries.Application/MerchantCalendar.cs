namespace CashFlow.Entries.Application;

/// <summary>The merchant accounting day follows Brazilian time (America/Sao_Paulo), not UTC.</summary>
public static class MerchantCalendar
{
    private static readonly TimeZoneInfo TimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static DateOnly Today(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZone).DateTime);
}
