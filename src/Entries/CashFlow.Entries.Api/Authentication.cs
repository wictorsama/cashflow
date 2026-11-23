using Microsoft.AspNetCore.Authentication.JwtBearer;
using CashFlow.Security;
using Microsoft.AspNetCore.Authorization;

namespace CashFlow.Entries.Api;

/// <summary>Scopes this API understands. A token must carry the scope an endpoint asks for.</summary>
public static class Scopes
{
    public const string EntriesWrite = "entries:write";
    public const string EntriesRead = "entries:read";
    /// <summary>Reads every merchant's totals. Held only by the reconciliation job.</summary>
    public const string EntriesReconcile = "entries:reconcile";
    /// <summary>Reads the entries of the merchants assigned to the auditor. Read-only.</summary>
    public const string EntriesAudit = "entries:audit";
    /// <summary>Either of the two ways to read entries: a merchant reading its own, or an auditor reading an assigned one.</summary>
    public const string EntriesReadOrAudit = "entries:read-or-audit";
}

public static class AuthenticationExtensions
{
    /// <summary>
    /// Every endpoint requires a valid JWT unless it is explicitly anonymous. The API validates the token itself
    /// (defence in depth): it never trusts that the gateway, or the network, did it.
    /// </summary>
    public static IServiceCollection AddCashFlowAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var metadataAddress = configuration["Auth:MetadataAddress"]
            ?? throw new InvalidOperationException("Auth:MetadataAddress is not configured.");
        var issuer = configuration["Auth:Issuer"]
            ?? throw new InvalidOperationException("Auth:Issuer is not configured.");
        var audience = configuration["Auth:Audience"]
            ?? throw new InvalidOperationException("Auth:Audience is not configured.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.MetadataAddress = metadataAddress;
            options.RequireHttpsMetadata = configuration.GetValue("Auth:RequireHttpsMetadata", true);
            options.MapInboundClaims = false; // keep claim names as issued ("scope", "merchant_id")
            options.TokenValidationParameters.ValidIssuer = issuer;
            options.TokenValidationParameters.ValidAudience = audience;
            options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
            // If the identity provider was not ready when this service first needed its keys (it restarts with the realm),
            // try again soon instead of refusing every token for the default five minutes.
            options.RefreshInterval = TimeSpan.FromSeconds(15);
        });

        var authorization = services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        foreach (var scope in AllScopes)
        {
            authorization.AddPolicy(scope, policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
                context.User.FindFirst("scope")?.Value.Split(' ').Contains(scope, StringComparer.Ordinal) == true));
        }

        authorization.AddPolicy(Scopes.EntriesReadOrAudit, policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
            CallerAccess.HasScope(context.User, Scopes.EntriesRead) || CallerAccess.HasScope(context.User, Scopes.EntriesAudit)));

        return services;
    }

    private static readonly string[] AllScopes = ["entries:write", "entries:read", "entries:reconcile", "entries:audit"];
}
