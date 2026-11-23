using System.Security.Claims;
using CashFlow.Consolidation.Application;

namespace CashFlow.Consolidation.Api;

/// <summary>The token is valid but says nothing about which merchant the caller acts for.</summary>
public sealed class MerchantClaimMissingException() : Exception("The token has no valid merchant_id claim.");

/// <summary>
/// Who the caller is. The merchant always comes from the validated token, never from the request: a caller cannot
/// ask for another merchant's data by changing a parameter or a header.
/// </summary>
public static class MerchantContext
{
    public const string ClaimName = "merchant_id";

    public static Guid Require(HttpContext http)
    {
        if (Guid.TryParse(http.User.FindFirstValue(ClaimName), out var merchantId) && merchantId != Guid.Empty)
        {
            return merchantId;
        }

        throw new MerchantClaimMissingException();
    }
}
