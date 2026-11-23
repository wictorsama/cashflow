using System.Security.Claims;
using CashFlow.Security;

namespace CashFlow.Security.Tests;

public sealed class CallerAccessTests
{
    private const string Audit = "balances:audit";
    private static readonly Guid Own = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Third = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static ClaimsPrincipal User(Guid? merchant = null, string scope = "", params Guid[] assigned)
    {
        var claims = new List<Claim> { new("sub", "someone"), new("scope", scope) };
        if (merchant is not null)
        {
            claims.Add(new Claim("merchant_id", merchant.Value.ToString()));
        }

        claims.AddRange(assigned.Select(id => new Claim("audit_merchants", id.ToString())));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    // ----- merchants

    [Fact]
    public void AMerchant_ReadsItsOwnData_WithoutNamingIt()
    {
        var access = CallerAccess.ResolveRead(User(Own), requestedMerchant: null, Audit);

        Assert.Equal(Own, access.MerchantId);
        Assert.False(access.IsAuditor);
    }

    [Fact]
    public void AMerchant_NamingItself_IsFine()
    {
        Assert.Equal(Own, CallerAccess.ResolveRead(User(Own), Own, Audit).MerchantId);
    }

    [Fact]
    public void AMerchant_NamingAnotherMerchant_IsRefused_NotAnsweredWithItsOwnData()
    {
        Assert.Throws<AccessDeniedException>(() => CallerAccess.ResolveRead(User(Own), Other, Audit));
    }

    [Fact]
    public void AMerchant_CannotBecomeAnAuditor_ByHoldingTheScopeAndAnAssignment()
    {
        // Even a token that carries both is a merchant: it still cannot read another merchant's data.
        var both = User(Own, Audit, Other);

        Assert.Throws<AccessDeniedException>(() => CallerAccess.ResolveRead(both, Other, Audit));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void AMalformedMerchantClaim_IsRefused(string claim)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("merchant_id", claim), new Claim("scope", Audit)], "test"));

        Assert.Throws<AccessDeniedException>(() => CallerAccess.ResolveRead(user, null, Audit));
    }

    // ----- auditors

    [Fact]
    public void AnAuditor_ReadsAnAssignedMerchant()
    {
        var access = CallerAccess.ResolveRead(User(null, Audit, Own, Other), Other, Audit);

        Assert.Equal(Other, access.MerchantId);
        Assert.True(access.IsAuditor);
    }

    [Fact]
    public void AnAuditor_CannotReadAMerchantThatIsNotAssigned()
    {
        Assert.Throws<AccessDeniedException>(() => CallerAccess.ResolveRead(User(null, Audit, Own), Third, Audit));
    }

    [Fact]
    public void AnAuditor_WithoutAnyAssignment_ReadsNothing()
    {
        Assert.Throws<AccessDeniedException>(() => CallerAccess.ResolveRead(User(null, Audit), Own, Audit));
    }

    [Fact]
    public void AnAuditor_MustNameTheMerchant_EvenWhenOnlyOneIsAssigned()
    {
        // No guessing: each request says whose data it is about, so the trail is exact.
        Assert.Throws<MerchantRequiredException>(() => CallerAccess.ResolveRead(User(null, Audit, Own), null, Audit));
    }

    [Fact]
    public void ANonMerchantWithoutTheAuditScope_IsRefused_EvenWithAnAssignment()
    {
        Assert.Throws<AccessDeniedException>(() => CallerAccess.ResolveRead(User(null, "balances:read", Own), Own, Audit));
    }

    [Fact]
    public void TheAuditScopeOfAnotherResource_DoesNotOpenThisOne()
    {
        Assert.Throws<AccessDeniedException>(() => CallerAccess.ResolveRead(User(null, "entries:audit", Own), Own, Audit));
    }

    [Fact]
    public void AnAssignmentThatIsNotAGuid_IsIgnored_AndNeverMatchesTheEmptyGuid()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("scope", Audit), new Claim("audit_merchants", "junk"), new Claim("audit_merchants", Guid.Empty.ToString())], "test"));

        Assert.Throws<AccessDeniedException>(() => CallerAccess.ResolveRead(user, Guid.Empty, Audit));
        Assert.Throws<AccessDeniedException>(() => CallerAccess.ResolveRead(user, Own, Audit));
    }

    [Fact]
    public void ScopeMatching_IsExact_NotASubstring()
    {
        Assert.False(CallerAccess.HasScope(User(null, "xbalances:audit"), Audit));
        Assert.True(CallerAccess.HasScope(User(null, "entries:read balances:audit"), Audit));
    }
}
