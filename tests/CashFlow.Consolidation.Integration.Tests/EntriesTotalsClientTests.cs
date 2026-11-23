using System.Net;
using System.Text;
using CashFlow.Consolidation.Infrastructure.Reconciliation;

namespace CashFlow.Consolidation.Integration.Tests;

/// <summary>The HTTP side of the job: it asks Keycloak for a token and uses it to read the Entries totals.</summary>
public sealed class EntriesTotalsClientTests
{
    private static readonly Guid Merchant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Authorization, string Body)> Calls { get; } = [];

        public int TokenCalls => Calls.Count(c => c.Url.Contains("/token", StringComparison.Ordinal));

        public int ExpiresInSeconds { get; set; } = 300;

        public HttpStatusCode TotalsStatus { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));

            if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
            {
                return Json($$"""{"access_token":"service-token","expires_in":{{ExpiresInSeconds}}}""");
            }

            return TotalsStatus == HttpStatusCode.OK
                ? Json($$"""[{"merchantId":"{{Merchant}}","date":"2026-11-17","credits":120.50,"debits":30.00}]""")
                : new HttpResponseMessage(TotalsStatus);
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly ReconciliationOptions Options = new()
    {
        EntriesUrl = "http://entries/",
        TokenUrl = "http://keycloak/realms/cashflow/protocol/openid-connect/token",
        ClientId = "reconciler",
        ClientSecret = "the-secret",
    };

    private static (EntriesTotalsClient Client, ScriptedHandler Handler, MovableClock Clock) Build()
    {
        var handler = new ScriptedHandler();
        var clock = new MovableClock(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));

        return (new EntriesTotalsClient(new HttpClient(handler), Options, clock), handler, clock);
    }

    [Fact]
    public async Task ReadsTheTotals_WithAServiceTokenFromTheClientCredentialsFlow()
    {
        var (client, handler, _) = Build();

        var totals = await client.GetAsync(new DateOnly(2026, 11, 17), new DateOnly(2026, 11, 17), DateTimeOffset.UtcNow, default);

        var total = Assert.Single(totals);
        Assert.Equal(120.50m, total.Credits);
        Assert.Equal(30m, total.Debits);

        var tokenRequest = handler.Calls[0];
        Assert.Equal(HttpMethod.Post, tokenRequest.Method);
        Assert.Contains("grant_type=client_credentials", tokenRequest.Body, StringComparison.Ordinal);
        Assert.Contains("client_id=reconciler", tokenRequest.Body, StringComparison.Ordinal);

        var dataRequest = handler.Calls[1];
        Assert.Equal("Bearer service-token", dataRequest.Authorization);
        Assert.StartsWith("http://entries/v1/reconciliation/daily-totals?from=2026-11-17&to=2026-11-17&registeredBefore=", dataRequest.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepsTheToken_UntilJustBeforeItExpires_ThenAsksForANewOne()
    {
        var (client, handler, clock) = Build();
        var day = new DateOnly(2026, 11, 17);

        await client.GetAsync(day, day, DateTimeOffset.UtcNow, default);
        await client.GetAsync(day, day, DateTimeOffset.UtcNow, default);
        Assert.Equal(1, handler.TokenCalls); // reused

        clock.Now = clock.Now.AddSeconds(290); // 300 s lifetime, renewed 30 s early
        await client.GetAsync(day, day, DateTimeOffset.UtcNow, default);
        Assert.Equal(2, handler.TokenCalls);
    }

    [Fact]
    public async Task AFailedAnswerFromEntries_IsAnError_NotAnEmptyList()
    {
        var (client, handler, _) = Build();
        handler.TotalsStatus = HttpStatusCode.Forbidden;
        var day = new DateOnly(2026, 11, 17);

        // An empty list would read as "Entries has nothing", and every balance would look wrong.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(day, day, DateTimeOffset.UtcNow, default));
    }
}
