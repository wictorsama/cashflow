using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace CashFlow.Gateway.Tests;

/// <summary>The gateway in front of a stub service that records what it receives.</summary>
public sealed class GatewayTests : IAsyncLifetime
{
    private static readonly Guid Merchant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private WebApplication _downstream = null!;
    private string _downstreamAddress = null!;

    // Snapshots: the server recycles the live header dictionary as soon as the request ends.
    private List<Dictionary<string, string>> Received { get; } = [];

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        _downstream = builder.Build();
        _downstream.Map("/{**path}", (HttpContext http) =>
        {
            lock (Received)
            {
                Received.Add(http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase));
            }

            return Results.Ok(new { path = http.Request.Path.Value });
        });

        await _downstream.StartAsync();
        _downstreamAddress = _downstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/";
    }

    public async Task DisposeAsync() => await _downstream.DisposeAsync();

    private WebApplicationFactory<Program> StartGateway(int permitPerSecond = 200) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            TestAuth.Configure(builder);
            builder.UseSetting("RateLimit:PermitPerSecond", permitPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("ReverseProxy:Clusters:entries:Destinations:primary:Address", _downstreamAddress);
            builder.UseSetting("ReverseProxy:Clusters:consolidation:Destinations:primary:Address", _downstreamAddress);
            builder.UseSetting("ReverseProxy:Clusters:assistant:Destinations:primary:Address", _downstreamAddress);
        });

    private static HttpRequestMessage Get(string url, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    [Fact]
    public async Task Request_WithoutToken_IsRejectedAtTheEdge_AndNeverReachesTheService()
    {
        await using var factory = StartGateway();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Get("/v1/entries", token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(Received);
    }

    [Fact]
    public async Task Request_WithForgedToken_IsRejected()
    {
        await using var factory = StartGateway();
        var client = factory.CreateClient();

        var forged = TestAuth.Token(Merchant, "entries:read", key: new SymmetricSecurityKey(new byte[32]));
        var response = await client.SendAsync(Get("/v1/entries", forged));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(Received);
    }

    [Theory]
    [InlineData("/v1/entries")]
    [InlineData("/v1/entries/abc")]
    [InlineData("/v1/balances/2026-11-17")]
    public async Task Request_WithValidToken_IsProxied_WithTheTokenAndACorrelationId(string path)
    {
        await using var factory = StartGateway();
        var client = factory.CreateClient();
        var token = TestAuth.Token(Merchant, "entries:read balances:read");

        var response = await client.SendAsync(Get(path, token));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var forwarded = Assert.Single(Received);
        Assert.Equal($"Bearer {token}", forwarded["Authorization"]); // the service validates it again
        Assert.Matches("^[0-9a-f]{32}$", forwarded["X-Correlation-Id"]);
        Assert.Equal(forwarded["X-Correlation-Id"], response.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task AssistantRoute_IsProxied_ForAValidToken_AndRefusedWithoutOne()
    {
        await using var factory = StartGateway();
        var client = factory.CreateClient();
        var anonymous = new HttpRequestMessage(HttpMethod.Post, "/v1/assistant/entry-drafts");
        var signed = new HttpRequestMessage(HttpMethod.Post, "/v1/assistant/entry-drafts");
        signed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(Merchant, "assistant:draft"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(anonymous)).StatusCode);
        Assert.Empty(Received);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(signed)).StatusCode);
        Assert.Single(Received);
    }

    [Fact]
    public async Task Route_ThatDoesNotExist_IsNotProxied()
    {
        await using var factory = StartGateway();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Get("/internal/secrets", TestAuth.Token(Merchant, "entries:read")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(Received);
    }

    [Fact]
    public async Task ReconciliationRoute_IsInternal_AndNeverReachesTheService_EvenWithAFullyScopedToken()
    {
        await using var factory = StartGateway();
        var client = factory.CreateClient();
        var token = TestAuth.Token(null, "entries:reconcile entries:read entries:write balances:rebuild");

        var totals = await client.SendAsync(Get("/v1/reconciliation/daily-totals?from=2026-11-17&to=2026-11-17", token));

        Assert.Equal(HttpStatusCode.NotFound, totals.StatusCode);
        Assert.Empty(Received);
    }

    [Fact]
    public async Task CorrelationId_FromTheCaller_IsKept_ButUnsafeValuesAreReplaced()
    {
        await using var factory = StartGateway();
        var client = factory.CreateClient();
        var token = TestAuth.Token(Merchant, "entries:read");

        var good = Get("/v1/entries", token);
        good.Headers.Add("X-Correlation-Id", "trace-12345678");
        var goodResponse = await client.SendAsync(good);

        var bad = Get("/v1/entries", token);
        bad.Headers.TryAddWithoutValidation("X-Correlation-Id", "abc def <script>");
        var badResponse = await client.SendAsync(bad);

        Assert.Equal("trace-12345678", goodResponse.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Matches("^[0-9a-f]{32}$", badResponse.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task Responses_CarrySecurityHeaders_AndNoServerHeader()
    {
        await using var factory = StartGateway();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", response.Headers.GetValues("Cache-Control").Single());
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task RateLimit_IsPerMerchant_AndOneMerchantCannotStarveAnother()
    {
        await using var factory = StartGateway(permitPerSecond: 5);
        var client = factory.CreateClient();
        var noisy = TestAuth.Token(Merchant, "entries:read");
        var quiet = TestAuth.Token(Guid.NewGuid(), "entries:read");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 20; i++)
        {
            statuses.Add((await client.SendAsync(Get("/v1/entries", noisy))).StatusCode);
        }

        var quietResponse = await client.SendAsync(Get("/v1/entries", quiet));

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Contains(HttpStatusCode.OK, statuses);
        Assert.Equal(HttpStatusCode.OK, quietResponse.StatusCode);
    }

    [Fact]
    public async Task Body_AboveTheLimit_IsRefused()
    {
        await using var factory = StartGateway();
        var client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/entries") { Content = new ByteArrayContent(new byte[200 * 1024]) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(Merchant, "entries:write"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(Received);
    }
}
