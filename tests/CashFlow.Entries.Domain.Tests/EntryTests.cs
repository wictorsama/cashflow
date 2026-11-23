using CashFlow.Entries.Domain;

namespace CashFlow.Entries.Domain.Tests;

public class EntryTests
{
    private static readonly Guid Merchant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Actor = "user-1";
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 11, 16);

    private static Entry NewEntry(
        EntryType type = EntryType.Debit,
        decimal amount = 150.00m,
        DateOnly? entryDate = null,
        string? description = "Electricity bill") =>
        Entry.Register(Merchant, type, amount, entryDate ?? Today, description, Actor, Now, Today);

    [Fact]
    public void Register_WithValidData_CreatesEntryAndRaisesEntryRegistered()
    {
        var entry = NewEntry(EntryType.Credit, 99.90m, new DateOnly(2026, 11, 15), "  Sale  ");

        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal(Merchant, entry.MerchantId);
        Assert.Equal(EntryType.Credit, entry.Type);
        Assert.Equal(99.90m, entry.Amount);
        Assert.Equal(new DateOnly(2026, 11, 15), entry.EntryDate);
        Assert.Equal("Sale", entry.Description);
        Assert.Equal(Actor, entry.CreatedBy);
        Assert.Equal(Now, entry.RegisteredAt);
        Assert.False(entry.IsReversal);
        Assert.Null(entry.ReversedBy);

        var registered = Assert.IsType<EntryRegistered>(Assert.Single(entry.DomainEvents));
        Assert.Equal(entry.Id, registered.EntryId);
        Assert.Equal(Merchant, registered.MerchantId);
        Assert.Equal(EntryType.Credit, registered.Type);
        Assert.Equal(99.90m, registered.Amount);
        Assert.Equal(entry.EntryDate, registered.EntryDate);
        Assert.Equal(Actor, registered.CreatedBy);
        Assert.NotEqual(Guid.Empty, registered.EventId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_WithoutAuthor_IsRejected(string? createdBy)
    {
        Assert.Throws<DomainException>(() =>
            Entry.Register(Merchant, EntryType.Debit, 10m, Today, "x", createdBy!, Now, Today));
    }

    [Fact]
    public void Register_AuthorTooLong_IsRejected()
    {
        Assert.Throws<DomainException>(() =>
            Entry.Register(Merchant, EntryType.Debit, 10m, Today, "x", new string('a', Entry.MaxCreatedByLength + 1), Now, Today));
    }

    [Fact]
    public void Reverse_RecordsWhoReversed_NotWhoRegistered()
    {
        var entry = NewEntry();

        var reversal = entry.Reverse(null, "user-2", Now);

        Assert.Equal(Actor, entry.CreatedBy); // the original keeps its author
        Assert.Equal("user-2", reversal.CreatedBy);
        var reversed = Assert.IsType<EntryReversed>(Assert.Single(reversal.DomainEvents));
        Assert.Equal("user-2", reversed.CreatedBy);
    }

    [Fact]
    public void Reverse_WithoutAuthor_IsRejected_AndLeavesTheEntryUntouched()
    {
        var entry = NewEntry();

        Assert.Throws<DomainException>(() => entry.Reverse(null, " ", Now));

        Assert.Null(entry.ReversedBy);
    }

    [Fact]
    public void Register_WithNullDescription_StoresEmptyDescription()
    {
        var entry = NewEntry(description: null);

        Assert.Equal(string.Empty, entry.Description);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.01)]
    public void Register_WithNonPositiveAmount_Throws(double amount)
    {
        Assert.Throws<DomainException>(() => NewEntry(amount: (decimal)amount));
    }

    [Fact]
    public void Register_WithMoreThanTwoDecimalPlaces_Throws()
    {
        Assert.Throws<DomainException>(() => NewEntry(amount: 10.005m));
    }

    [Fact]
    public void Register_WithAmountAboveMaximum_Throws()
    {
        Assert.Throws<DomainException>(() => NewEntry(amount: Entry.MaxAmount + 0.01m));
    }

    [Fact]
    public void Register_WithMaximumAmount_Succeeds()
    {
        var entry = NewEntry(amount: Entry.MaxAmount);

        Assert.Equal(Entry.MaxAmount, entry.Amount);
    }

    [Fact]
    public void Register_WithFutureDate_Throws()
    {
        Assert.Throws<DomainException>(() => NewEntry(entryDate: Today.AddDays(1)));
    }

    [Fact]
    public void Register_WithPastDate_Succeeds()
    {
        var entry = NewEntry(entryDate: Today.AddDays(-30));

        Assert.Equal(Today.AddDays(-30), entry.EntryDate);
    }

    [Fact]
    public void Register_WithEmptyMerchant_Throws()
    {
        Assert.Throws<DomainException>(() =>
            Entry.Register(Guid.Empty, EntryType.Debit, 10m, Today, "x", Actor, Now, Today));
    }

    [Fact]
    public void Register_WithUndefinedType_Throws()
    {
        Assert.Throws<DomainException>(() =>
            Entry.Register(Merchant, (EntryType)99, 10m, Today, "x", Actor, Now, Today));
    }

    [Fact]
    public void Register_WithTooLongDescription_Throws()
    {
        var description = new string('a', Entry.MaxDescriptionLength + 1);

        Assert.Throws<DomainException>(() => NewEntry(description: description));
    }

    [Fact]
    public void Register_WithMaxLengthDescription_Succeeds()
    {
        var description = new string('a', Entry.MaxDescriptionLength);

        Assert.Equal(description, NewEntry(description: description).Description);
    }

    [Theory]
    [InlineData(EntryType.Debit, EntryType.Credit)]
    [InlineData(EntryType.Credit, EntryType.Debit)]
    public void Reverse_CreatesCompensatingEntryOfOppositeType(EntryType original, EntryType expected)
    {
        var entry = NewEntry(original, 150.00m, new DateOnly(2026, 11, 10));
        var later = Now.AddHours(1);

        var reversal = entry.Reverse("Wrong value", Actor, later);

        Assert.Equal(expected, reversal.Type);
        Assert.Equal(entry.Amount, reversal.Amount);
        Assert.Equal(entry.EntryDate, reversal.EntryDate);
        Assert.Equal(entry.MerchantId, reversal.MerchantId);
        Assert.Equal("Wrong value", reversal.Description);
        Assert.Equal(later, reversal.RegisteredAt);
        Assert.True(reversal.IsReversal);
        Assert.Equal(entry.Id, reversal.ReversalOf);
        Assert.NotEqual(entry.Id, reversal.Id);
        Assert.Equal(reversal.Id, entry.ReversedBy);
    }

    [Fact]
    public void Reverse_RaisesEntryReversedWithCompensatingEntryData()
    {
        var entry = NewEntry(EntryType.Debit, 80m);

        var reversal = entry.Reverse(null, Actor, Now);

        var reversed = Assert.IsType<EntryReversed>(Assert.Single(reversal.DomainEvents));
        Assert.Equal(reversal.Id, reversed.EntryId);
        Assert.Equal(entry.Id, reversed.OriginalEntryId);
        Assert.Equal(EntryType.Credit, reversed.Type);
        Assert.Equal(80m, reversed.Amount);
        Assert.Equal(entry.EntryDate, reversed.EntryDate);
        Assert.Equal(Merchant, reversed.MerchantId);
    }

    [Fact]
    public void Reverse_WithoutReason_UsesDefaultDescription()
    {
        var entry = NewEntry();

        var reversal = entry.Reverse("   ", Actor, Now);

        Assert.Equal($"Reversal of entry {entry.Id}", reversal.Description);
    }

    [Fact]
    public void Reverse_Twice_Throws()
    {
        var entry = NewEntry();
        entry.Reverse(null, Actor, Now);

        Assert.Throws<EntryAlreadyReversedException>(() => entry.Reverse(null, Actor, Now));
    }

    [Fact]
    public void Reverse_OfAReversal_Throws()
    {
        var reversal = NewEntry().Reverse(null, Actor, Now);

        Assert.Throws<DomainException>(() => reversal.Reverse(null, Actor, Now));
    }

    [Fact]
    public void Reverse_WithTooLongReason_ThrowsAndLeavesEntryUnchanged()
    {
        var entry = NewEntry();
        var reason = new string('a', Entry.MaxDescriptionLength + 1);

        Assert.Throws<DomainException>(() => entry.Reverse(reason, Actor, Now));
        Assert.Null(entry.ReversedBy);
    }

    [Fact]
    public void ClearDomainEvents_RemovesPendingEvents()
    {
        var entry = NewEntry();

        entry.ClearDomainEvents();

        Assert.Empty(entry.DomainEvents);
    }
}
