using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace CashFlow.Security;

/// <summary>The caller may not see what it asked for.</summary>
public sealed class AccessDeniedException(string message) : Exception(message);

/// <summary>An auditor reads on behalf of a merchant it must name.</summary>
public sealed class MerchantRequiredException() : Exception("Query parameter 'merchantId' is required for an auditor.");

/// <summary>Whose data a read is about, and whether the caller is an auditor looking at someone else's data.</summary>
public sealed record ReadAccess(Guid MerchantId, bool IsAuditor);

/// <summary>
/// Decides whose data a read may touch.
///
/// A merchant sees only its own data: the merchant comes from the validated token, and a request that names a
/// different one is refused (not silently answered with the caller's own data, which would hide the attempt).
///
/// An auditor has no merchant of its own. It reads the data of the merchants **assigned to it** in the token
/// (claim <see cref="AuditMerchantsClaim"/>, set by the identity provider and not changeable by the caller), and
/// must say which one in each request. Holding the audit scope is not enough: an auditor with no assignment, or
/// asking for a merchant outside its assignment, is refused. The role is read-only by construction, because every
/// write needs a merchant of its own.
/// </summary>
public static class CallerAccess
{
    public const string MerchantClaim = "merchant_id";
    public const string AuditMerchantsClaim = "audit_merchants";
    public const string SubjectClaim = "sub";

    public static ReadAccess ResolveRead(ClaimsPrincipal user, Guid? requestedMerchant, string auditScope)
    {
        var own = user.FindFirstValue(MerchantClaim);
        if (own is not null)
        {
            if (!Guid.TryParse(own, out var merchantId) || merchantId == Guid.Empty)
            {
                throw new AccessDeniedException($"The token has no valid {MerchantClaim} claim.");
            }

            if (requestedMerchant is not null && requestedMerchant != merchantId)
            {
                throw new AccessDeniedException("A merchant can only read its own data.");
            }

            return new ReadAccess(merchantId, IsAuditor: false);
        }

        if (!HasScope(user, auditScope))
        {
            throw new AccessDeniedException($"The token has no {MerchantClaim} claim and no {auditScope} scope.");
        }

        if (requestedMerchant is null)
        {
            throw new MerchantRequiredException();
        }

        var assigned = user.FindAll(AuditMerchantsClaim)
            .Select(c => Guid.TryParse(c.Value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty);

        if (!assigned.Contains(requestedMerchant.Value))
        {
            throw new AccessDeniedException("This merchant is not assigned to the auditor.");
        }

        return new ReadAccess(requestedMerchant.Value, IsAuditor: true);
    }

    public static bool HasScope(ClaimsPrincipal user, string scope) =>
        user.FindFirst("scope")?.Value.Split(' ').Contains(scope, StringComparer.Ordinal) == true;
}

/// <summary>
/// Every read by an auditor leaves a trace: who, which merchant, which resource and when (the log line carries the
/// trace id and the correlation id). The counter has no merchant label, so its cardinality stays bounded.
/// </summary>
public static partial class AuditTrail
{
    public const string MeterName = "CashFlow.Security";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Accesses = Meter.CreateCounter<long>(
        "cashflow.audit.accesses", description: "Reads of a merchant's data by an auditor, by resource and outcome.");

    public static void Granted(ILogger logger, ClaimsPrincipal user, Guid merchantId, string resource)
    {
        Accesses.Add(1, new("resource", resource), new("outcome", "granted"));
        var auditor = user.FindFirstValue(CallerAccess.SubjectClaim) ?? "unknown";
        LogGranted(logger, auditor, merchantId, resource);
    }

    public static void Denied(ILogger logger, ClaimsPrincipal user, string resource, string reason)
    {
        Accesses.Add(1, new("resource", resource), new("outcome", "denied"));
        var auditor = user.FindFirstValue(CallerAccess.SubjectClaim) ?? "unknown";
        LogDenied(logger, auditor, resource, reason);
    }

    [LoggerMessage(EventId = 9001, Level = LogLevel.Information, Message = "AUDIT access granted: {Auditor} read {Resource} of merchant {MerchantId}.")]
    private static partial void LogGranted(ILogger logger, string auditor, Guid merchantId, string resource);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Warning, Message = "AUDIT access denied: {Auditor} asked for {Resource}: {Reason}")]
    private static partial void LogDenied(ILogger logger, string auditor, string resource, string reason);
}

public static class CallerAccessHttpExtensions
{
    /// <summary>
    /// Resolves whose data the request may read and records the outcome: every auditor read, and every refusal.
    /// A merchant reading its own data is the normal case and is not recorded here.
    /// </summary>
    public static ReadAccess ResolveRead(this HttpContext http, Guid? requestedMerchant, string auditScope, string resource)
    {
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CashFlow.Audit");

        ReadAccess access;
        try
        {
            access = CallerAccess.ResolveRead(http.User, requestedMerchant, auditScope);
        }
        catch (AccessDeniedException e)
        {
            AuditTrail.Denied(logger, http.User, resource, e.Message);
            throw;
        }

        if (access.IsAuditor)
        {
            AuditTrail.Granted(logger, http.User, access.MerchantId, resource);
        }

        return access;
    }
}
