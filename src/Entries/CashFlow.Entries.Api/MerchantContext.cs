using System.Security.Claims;
using CashFlow.Entries.Application;

namespace CashFlow.Entries.Api;

/// <summary>The token is valid but says nothing about which merchant the caller acts for.</summary>
public sealed class MerchantClaimMissingException(string claim = MerchantContext.ClaimName)
    : Exception($"The token has no valid {claim} claim.");

/// <summary>
/// Who the caller is. The merchant always comes from the validated token, never from the request: a caller cannot
/// ask for another merchant's data by changing a parameter or a header.
/// </summary>
public static class MerchantContext
{
    public const string ClaimName = "merchant_id";
    public const string SubjectClaim = "sub";

    public static Guid Require(HttpContext http)
    {
        if (Guid.TryParse(http.User.FindFirstValue(ClaimName), out var merchantId) && merchantId != Guid.Empty)
        {
            return merchantId;
        }

        throw new MerchantClaimMissingException();
    }

    /// <summary>Who is acting: the subject of the token (a person or an integration). Recorded in every entry.</summary>
    public static string RequireActor(HttpContext http) =>
        http.User.FindFirstValue(SubjectClaim) is { Length: > 0 } subject
            ? subject
            : throw new MerchantClaimMissingException(SubjectClaim);
}
