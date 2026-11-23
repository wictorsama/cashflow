using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CashFlow.Consolidation.Integration.Tests;

/// <summary>
/// Stands in for Keycloak: the API validates real signed JWTs, only the signing key comes from the test instead of
/// the identity provider's JWKS endpoint. Everything else (issuer, audience, expiry, scopes) is the production path.
/// </summary>
internal static class TestAuth
{
    public const string Issuer = "https://idp.test/realms/cashflow";
    public const string Audience = "cashflow-api";

    private static readonly SymmetricSecurityKey SigningKey = new(RandomNumberGenerator.GetBytes(32)) { KeyId = "test" };

    public static void Configure(IWebHostBuilder builder)
    {
        builder.UseSetting("Auth:MetadataAddress", Issuer + "/.well-known/openid-configuration");
        builder.UseSetting("Auth:Issuer", Issuer);
        builder.UseSetting("Auth:Audience", Audience);

        builder.ConfigureTestServices(services => services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            var configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
            configuration.SigningKeys.Add(SigningKey);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
        }));
    }

    public static string Token(
        Guid? merchant, string scopes, string audience = Audience, string issuer = Issuer, TimeSpan? lifetime = null, SecurityKey? key = null, IEnumerable<Guid>? auditMerchants = null)
    {
        var claims = new Dictionary<string, object> { ["scope"] = scopes, ["sub"] = "test-user" };
        if (merchant is not null)
        {
            claims["merchant_id"] = merchant.Value.ToString();
        }

        if (auditMerchants is not null)
        {
            claims["audit_merchants"] = auditMerchants.Select(id => id.ToString()).ToArray();
        }

        var now = DateTime.UtcNow;
        var lifetimeValue = lifetime ?? TimeSpan.FromMinutes(5);

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Claims = claims,
            NotBefore = lifetimeValue < TimeSpan.Zero ? now + lifetimeValue - TimeSpan.FromMinutes(5) : now,
            Expires = now + lifetimeValue,
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.HmacSha256),
        });
    }

    public static AuthenticationHeaderValue Bearer(Guid? merchant, string scopes, IEnumerable<Guid>? auditMerchants = null) =>
        new("Bearer", Token(merchant, scopes, auditMerchants: auditMerchants));
}
