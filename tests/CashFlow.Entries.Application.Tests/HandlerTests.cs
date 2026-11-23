using CashFlow.Entries.Domain;

namespace CashFlow.Entries.Application.Tests;

public class HandlerTests
{
    private static readonly Guid Merchant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Actor = "user-1";

    // 2026-11-17 01:30 UTC is still 2026-11-16 22:30 in Sao Paulo (UTC-3).
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 1, 30, 0, TimeSpan.Zero);
    private static readonly DateOnly MerchantToday = new(2026, 11, 16);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static (RegisterEntryHandler Register, ReverseEntryHandler Reverse, FakeEntryStore Store) Build()
    {
        var store = new FakeEntryStore();
        var clock = new FixedClock(Now);
        return (new RegisterEntryHandler(store, store, clock), new ReverseEntryHandler(store, store, clock), store);
    }

    [Fact]
    public async Task Register_PersistsEntryAndSavesOnce()
    {
        var (register, _, store) = Build();

        var dto = await register.HandleAsync(
            new RegisterEntryCommand(Merchant, Actor, EntryType.Credit, 100m, MerchantToday, "Sale"), default);

        Assert.Equal(100m, dto.Amount);
        Assert.Single(store.Entries);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task Register_RecordsTheAuthorOnTheEntry()
    {
        var (register, _, store) = Build();

        var dto = await register.HandleAsync(
            new RegisterEntryCommand(Merchant, "ana", EntryType.Credit, 100m, MerchantToday, "Sale"), default);

        Assert.Equal("ana", dto.CreatedBy);
        Assert.Equal("ana", Assert.Single(store.Entries).CreatedBy);
    }

    [Fact]
    public async Task Reverse_RecordsWhoReversed_AndLeavesTheOriginalAuthorAlone()
    {
        var (register, reverse, store) = Build();
        var original = await register.HandleAsync(
            new RegisterEntryCommand(Merchant, "ana", EntryType.Debit, 50m, MerchantToday, "Bill"), default);

        var reversal = await reverse.HandleAsync(new ReverseEntryCommand(Merchant, "bruno", original.Id, "Wrong"), default);

        Assert.Equal("bruno", reversal.CreatedBy);
        Assert.Equal("ana", store.Entries.Single(e => e.Id == original.Id).CreatedBy);
    }

    [Fact]
    public async Task Register_UsesMerchantDayNotUtcDay()
    {
        var (register, _, _) = Build();

        // Today in UTC is the 17th, but for the merchant it is still the 16th: the 17th is the future.
        await Assert.ThrowsAsync<DomainException>(() => register.HandleAsync(
            new RegisterEntryCommand(Merchant, Actor, EntryType.Debit, 10m, new DateOnly(2026, 11, 17), null), default));

        var dto = await register.HandleAsync(
            new RegisterEntryCommand(Merchant, Actor, EntryType.Debit, 10m, MerchantToday, null), default);
        Assert.Equal(MerchantToday, dto.EntryDate);
    }

    [Fact]
    public async Task Register_WhenDomainRejects_DoesNotSave()
    {
        var (register, _, store) = Build();

        await Assert.ThrowsAsync<DomainException>(() => register.HandleAsync(
            new RegisterEntryCommand(Merchant, Actor, EntryType.Debit, -5m, MerchantToday, null), default));

        Assert.Empty(store.Entries);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task Reverse_AddsCompensatingEntryAndMarksOriginal()
    {
        var (register, reverse, store) = Build();
        var original = await register.HandleAsync(
            new RegisterEntryCommand(Merchant, Actor, EntryType.Debit, 50m, MerchantToday, "Bill"), default);

        var reversal = await reverse.HandleAsync(new ReverseEntryCommand(Merchant, Actor, original.Id, "Wrong"), default);

        Assert.Equal(EntryType.Credit, reversal.Type);
        Assert.Equal(original.Id, reversal.ReversalOf);
        Assert.Equal(2, store.Entries.Count);
        Assert.Equal(reversal.Id, store.Entries.First(e => e.Id == original.Id).ReversedBy);
        Assert.Equal(2, store.SaveCount);
    }

    [Fact]
    public async Task Reverse_UnknownEntry_Throws()
    {
        var (_, reverse, _) = Build();

        await Assert.ThrowsAsync<EntryNotFoundException>(() =>
            reverse.HandleAsync(new ReverseEntryCommand(Merchant, Actor, Guid.NewGuid(), null), default));
    }

    [Fact]
    public async Task Reverse_EntryOfAnotherMerchant_IsNotFound()
    {
        var (register, reverse, _) = Build();
        var original = await register.HandleAsync(
            new RegisterEntryCommand(Merchant, Actor, EntryType.Debit, 50m, MerchantToday, null), default);

        await Assert.ThrowsAsync<EntryNotFoundException>(() =>
            reverse.HandleAsync(new ReverseEntryCommand(Guid.NewGuid(), Actor, original.Id, null), default));
    }

    [Fact]
    public async Task Reverse_Twice_Throws()
    {
        var (register, reverse, _) = Build();
        var original = await register.HandleAsync(
            new RegisterEntryCommand(Merchant, Actor, EntryType.Debit, 50m, MerchantToday, null), default);
        await reverse.HandleAsync(new ReverseEntryCommand(Merchant, Actor, original.Id, null), default);

        await Assert.ThrowsAsync<EntryAlreadyReversedException>(() =>
            reverse.HandleAsync(new ReverseEntryCommand(Merchant, Actor, original.Id, null), default));
    }
}
