using System.Security.Claims;
using System.Threading.RateLimiting;
using CashFlow.Gateway;
using CashFlow.Observability;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.AddCashFlowObservability("cashflow-gateway", ["Yarp.ReverseProxy"], ["Yarp.ReverseProxy", "CashFlow.Gateway"]);

var metadataAddress = builder.Configuration["Auth:MetadataAddress"]
    ?? throw new InvalidOperationException("Auth:MetadataAddress is not configured.");
var issuer = builder.Configuration["Auth:Issuer"]
    ?? throw new InvalidOperationException("Auth:Issuer is not configured.");
var audience = builder.Configuration["Auth:Audience"]
    ?? throw new InvalidOperationException("Auth:Audience is not configured.");

// The gateway rejects bad tokens at the edge, but each service validates again and enforces scopes (defence in depth).
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MetadataAddress = metadataAddress;
    options.RequireHttpsMetadata = builder.Configuration.GetValue("Auth:RequireHttpsMetadata", true);
    options.MapInboundClaims = false;
    options.TokenValidationParameters.ValidIssuer = issuer;
    options.TokenValidationParameters.ValidAudience = audience;
    options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
    // Same reason as in the services: recover quickly when the identity provider was not ready at the first request.
    options.RefreshInterval = TimeSpan.FromSeconds(15);
});

builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

// One bucket per merchant (or per client address when the caller is anonymous). The limit sits well above the legitimate
// peak (50 req/s), because a 429 counts as a lost request for the business: it only stops abuse.
var permitPerSecond = builder.Configuration.GetValue("RateLimit:PermitPerSecond", 200);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.User.FindFirstValue("merchant_id") ?? http.User.FindFirstValue("sub") ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitPerSecond,
                Window = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
            }));
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "1";
        return ValueTask.CompletedTask;
    };
});

builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = EdgeHeadersMiddleware.MaxBodyBytes;
});

var app = builder.Build();

app.UseMiddleware<EdgeHeadersMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<RetryMiddleware>(); // after the rate limiter: a retry is not a new request from the caller

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapReverseProxy();

await app.RunAsync();

/// <summary>Exposed so the tests can start the whole gateway in memory.</summary>
public partial class Program;
