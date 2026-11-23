using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using CashFlow.Assistant.Api;
using CashFlow.Assistant.Core;
using CashFlow.Observability;
using CashFlow.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.AddCashFlowObservability("cashflow-assistant", [AssistantTelemetry.Name], [AssistantTelemetry.Name]);

var metadataAddress = builder.Configuration["Auth:MetadataAddress"]
    ?? throw new InvalidOperationException("Auth:MetadataAddress is not configured.");
var issuer = builder.Configuration["Auth:Issuer"]
    ?? throw new InvalidOperationException("Auth:Issuer is not configured.");
var audience = builder.Configuration["Auth:Audience"]
    ?? throw new InvalidOperationException("Auth:Audience is not configured.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MetadataAddress = metadataAddress;
    options.RequireHttpsMetadata = builder.Configuration.GetValue("Auth:RequireHttpsMetadata", true);
    options.MapInboundClaims = false;
    options.TokenValidationParameters.ValidIssuer = issuer;
    options.TokenValidationParameters.ValidAudience = audience;
    options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
    options.RefreshInterval = TimeSpan.FromSeconds(15);
});

const string DraftScope = "assistant:draft";
const long MaxBodyBytes = 8 * 1024;
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(DraftScope, policy => policy.RequireAuthenticatedUser().RequireAssertion(context => CallerAccess.HasScope(context.User, DraftScope)));

// A model call costs money and time: each merchant has its own small allowance (RNF-17).
var perMinute = builder.Configuration.GetValue("Assistant:RequestsPerMinute", 20);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.User.FindFirstValue(CallerAccess.MerchantClaim) ?? http.User.FindFirstValue("sub") ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

// The provider sits behind an interface. "rules" needs no key and no network (development, tests, fallback);
// "claude" calls the Claude API and needs a key, so it is chosen on purpose and fails fast if the key is missing.
var provider = builder.Configuration["Assistant:Provider"] ?? "rules";
switch (provider)
{
    case "rules":
        builder.Services.AddSingleton<IEntryInterpreter, RuleBasedInterpreter>();
        break;
    case "claude":
        var key = builder.Configuration["Assistant:Claude:ApiKey"];
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("Assistant:Provider is 'claude' but Assistant:Claude:ApiKey is not set.");
        }

        var claude = new ClaudeOptions
        {
            ApiKey = key,
            Model = builder.Configuration["Assistant:Claude:Model"] ?? "claude-haiku-5-5",
            BaseUrl = builder.Configuration["Assistant:Claude:BaseUrl"] ?? "https://api.anthropic.com/",
            Timeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Assistant:Claude:TimeoutSeconds", 4)),
        };
        builder.Services.AddHttpClient("claude");
        builder.Services.AddSingleton<IEntryInterpreter>(sp => new ClaudeInterpreter(sp.GetRequiredService<IHttpClientFactory>().CreateClient("claude"), claude));
        break;
    default:
        throw new InvalidOperationException($"Assistant:Provider '{provider}' is not supported (use 'rules' or 'claude').");
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DraftService>();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
});
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    kestrel.Limits.MaxRequestBodySize = MaxBodyBytes; // a sentence, not a document
});

var app = builder.Build();

// A sentence is short: refuse anything bigger before reading it (Kestrel enforces the same limit for chunked bodies).
app.Use(async (context, next) =>
{
    if (context.Request.ContentLength > MaxBodyBytes)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        return;
    }

    await next(context);
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" })).AllowAnonymous();
app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" })).AllowAnonymous();

// The assistant proposes; it never records. Whoever calls this must be a merchant (an auditor has no entries to draft),
// and the draft goes back to the person, who confirms it through the ordinary POST /v1/entries.
app.MapPost("/v1/assistant/entry-drafts", async (HttpContext http, DraftRequest request, DraftService service, CancellationToken cancellationToken) =>
{
    if (http.User.FindFirstValue(CallerAccess.MerchantClaim) is null)
    {
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Forbidden", detail: "Only a merchant can ask for an entry draft.");
    }

    try
    {
        var result = await service.DraftAsync(request.Text, service.Today(), cancellationToken);

        return Results.Ok(result);
    }
    catch (AssistantUnavailableException)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Assistant unavailable",
            detail: "The assistant could not answer now. Use the form: registering entries is not affected.");
    }
}).RequireAuthorization(DraftScope);

await app.RunAsync();

/// <summary>Exposed so the tests can start the whole service in memory.</summary>
public partial class Program;
