using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CashFlow.Entries.Application;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;

namespace CashFlow.Entries.Integration.Tests;

public sealed class ApiTests(InfrastructureFixture infra) : IClassFixture<InfrastructureFixture>
{
    private static readonly Guid Merchant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherMerchant = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static string Today => MerchantCalendar.Today(DateTimeOffset.UtcNow).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<(WebApplicationFactory<Program> Factory, HttpClient Client)> StartAsync()
    {
        var connection = await infra.CreateDatabaseAsync();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", connection);
            builder.UseSetting("RabbitMq:Uri", infra.RabbitMqUri);
            TestAuth.Configure(builder);
        });

        return (factory, factory.CreateClient());
    }

    private static HttpRequestMessage Post(string url, object? body, string? key, Guid? merchant = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        request.Headers.Authorization = TestAuth.Bearer(merchant == Guid.Empty ? null : merchant ?? Merchant, "entries:write entries:read");

        return request;
    }

    private static HttpRequestMessage Get(string url, Guid? merchant = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = TestAuth.Bearer(merchant ?? Merchant, "entries:write entries:read");
        return request;
    }

    private static object NewEntry(decimal amount = 100m, string? date = null) =>
        new { type = "Credit", amount, entryDate = date ?? Today, description = "Sale" };

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task Register_ThenGet_ReturnsTheEntry()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var created = await client.SendAsync(Post("/v1/entries", NewEntry(99.90m), "key-1"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await ReadAsync(created);
        var id = body.GetProperty("id").GetGuid();
        Assert.Equal("Credit", body.GetProperty("type").GetString());
        Assert.Equal(99.90m, body.GetProperty("amount").GetDecimal());

        var fetched = await client.SendAsync(Get($"/v1/entries/{id}"));
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(id, (await ReadAsync(fetched)).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Register_SameKeyAndRequest_ReplaysWithoutCreatingAnotherEntry()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var first = await client.SendAsync(Post("/v1/entries", NewEntry(), "retry-key"));
        var second = await client.SendAsync(Post("/v1/entries", NewEntry(), "retry-key"));

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        Assert.False(first.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(
            (await ReadAsync(first)).GetProperty("id").GetGuid(),
            (await ReadAsync(second)).GetProperty("id").GetGuid());

        var list = await ReadAsync(await client.SendAsync(Get($"/v1/entries?from={Today}&to={Today}")));
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Register_SameKeyDifferentRequest_IsRejected()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        await client.SendAsync(Post("/v1/entries", NewEntry(10m), "shared-key"));
        var other = await client.SendAsync(Post("/v1/entries", NewEntry(20m), "shared-key"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, other.StatusCode);
    }

    [Fact]
    public async Task Register_TenConcurrentRetriesWithSameKey_CreateExactlyOneEntry()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var responses = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => client.SendAsync(Post("/v1/entries", NewEntry(), "concurrent-key"))));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = new HashSet<Guid>();
        foreach (var response in responses)
        {
            ids.Add((await ReadAsync(response)).GetProperty("id").GetGuid());
        }

        Assert.Single(ids);

        var list = await ReadAsync(await client.SendAsync(Get($"/v1/entries?from={Today}&to={Today}")));
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Register_WithoutIdempotencyKey_Returns400()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var response = await client.SendAsync(Post("/v1/entries", NewEntry(), key: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static HttpRequestMessage PostWithToken(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/entries") { Content = JsonContent.Create(NewEntry()) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Entries_RecordWhoRegisteredAndWhoReversed()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        // Ana registers; Bruno (same merchant, another user) reverses.
        var register = Post("/v1/entries", NewEntry(80m), "who-1");
        register.Headers.Authorization = TestAuth.Bearer(Merchant, "entries:write entries:read", subject: "ana");
        var created = await ReadAsync(await client.SendAsync(register));
        var id = created.GetProperty("id").GetGuid();

        var reverse = Post($"/v1/entries/{id}/reversals", new { reason = "Wrong value" }, "who-2");
        reverse.Headers.Authorization = TestAuth.Bearer(Merchant, "entries:write entries:read", subject: "bruno");
        var reversal = await ReadAsync(await client.SendAsync(reverse));

        Assert.Equal("ana", created.GetProperty("createdBy").GetString());
        Assert.Equal("bruno", reversal.GetProperty("createdBy").GetString());

        // The history shows both, and the original still names Ana.
        var original = await ReadAsync(await client.SendAsync(Get($"/v1/entries/{id}")));
        Assert.Equal("ana", original.GetProperty("createdBy").GetString());
        var theReversal = await ReadAsync(await client.SendAsync(Get($"/v1/entries/{reversal.GetProperty("id").GetGuid()}")));
        Assert.Equal("bruno", theReversal.GetProperty("createdBy").GetString());
    }

    [Fact]
    public async Task Register_AuthorComesFromTheToken_NotFromTheRequestBody()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var request = Post("/v1/entries", new { type = "Credit", amount = 10m, entryDate = Today, description = "x", createdBy = "someone-else" }, "spoof-author");
        var created = await ReadAsync(await client.SendAsync(request));

        Assert.Equal("test-user", created.GetProperty("createdBy").GetString());
    }

    [Fact]
    public async Task Register_WithoutTheSubjectClaim_Returns403()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;
        var token = TestAuth.Token(Merchant, "entries:write", subject: "");

        var response = await client.SendAsync(PostWithToken(token));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static HttpRequestMessage Totals(string query, string scopes, bool withMerchant = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/v1/reconciliation/daily-totals" + query);
        request.Headers.Authorization = TestAuth.Bearer(withMerchant ? Merchant : null, scopes);

        return request;
    }

    [Fact]
    public async Task ReconciliationTotals_AreGroupedPerMerchantAndDay_ForTheJobScopeOnly()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;
        await client.SendAsync(Post("/v1/entries", NewEntry(100m), "t-1"));
        await client.SendAsync(Post("/v1/entries", new { type = "Debit", amount = 30m, entryDate = Today, description = "x" }, "t-2"));
        await client.SendAsync(Post("/v1/entries", NewEntry(5m), "t-3", OtherMerchant));
        var query = $"?from={Today}&to={Today}&registeredBefore={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddMinutes(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture))}";

        var totals = await ReadAsync(await client.SendAsync(Totals(query, "entries:reconcile")));

        Assert.Equal(2, totals.GetArrayLength()); // one row per merchant and day
        var mine = totals.EnumerateArray().Single(t => t.GetProperty("merchantId").GetGuid() == Merchant);
        Assert.Equal(100m, mine.GetProperty("credits").GetDecimal());
        Assert.Equal(30m, mine.GetProperty("debits").GetDecimal());

        // Only the job's scope reads every merchant: a merchant's own scopes are not enough, and neither is no token.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Totals(query, "entries:read entries:write", withMerchant: true))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/reconciliation/daily-totals" + query)).StatusCode);
    }

    [Fact]
    public async Task ReconciliationTotals_LeaveOutEntriesRegisteredAfterTheCutoff()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;
        await client.SendAsync(Post("/v1/entries", NewEntry(100m), "cut-1"));
        var longAgo = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture));

        var totals = await ReadAsync(await client.SendAsync(Totals($"?from={Today}&to={Today}&registeredBefore={longAgo}", "entries:reconcile")));

        Assert.Equal(0, totals.GetArrayLength()); // still in transit as far as the job is concerned
    }

    [Theory]
    [InlineData("?to=2026-11-17")]
    [InlineData("?from=2026-11-17&to=2026-11-10")]
    [InlineData("?from=2026-01-01&to=2026-12-31")]
    public async Task ReconciliationTotals_RejectAnInvalidPeriod(string query)
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var response = await client.SendAsync(Totals(query, "entries:reconcile"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithoutToken_Returns401()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/entries") { Content = JsonContent.Create(NewEntry()) };
        request.Headers.Add("Idempotency-Key", "k");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Register_WithInvalidToken_Returns401()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var scopes = "entries:write";
        var invalid = new[]
        {
            TestAuth.Token(Merchant, scopes, lifetime: TimeSpan.FromMinutes(-10)),                                   // expired
            TestAuth.Token(Merchant, scopes, audience: "another-api"),                                              // wrong audience
            TestAuth.Token(Merchant, scopes, issuer: "https://evil.example/realms/cashflow"),                        // wrong issuer
            TestAuth.Token(Merchant, scopes, key: new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32))),     // forged signature
            "not-a-jwt",
        };

        foreach (var token in invalid)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(PostWithToken(token))).StatusCode);
        }
    }

    [Fact]
    public async Task Register_WithoutTheWriteScope_Returns403()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var response = await client.SendAsync(PostWithToken(TestAuth.Token(Merchant, "entries:read")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Register_TokenWithoutMerchantClaim_Returns403()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var response = await client.SendAsync(Post("/v1/entries", NewEntry(), "k", merchant: Guid.Empty));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Merchant_ComesFromTheToken_NotFromAHeader()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var request = Post("/v1/entries", NewEntry(), "spoof");
        request.Headers.Add("X-Merchant-Id", OtherMerchant.ToString());
        var created = await ReadAsync(await client.SendAsync(request));

        Assert.Equal(Merchant, created.GetProperty("merchantId").GetGuid());
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(10.123)]
    public async Task Register_WithInvalidAmount_Returns422(double amount)
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var response = await client.SendAsync(Post("/v1/entries", NewEntry((decimal)amount), "bad-amount"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Register_WithFutureDate_Returns422()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var future = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var response = await client.SendAsync(Post("/v1/entries", NewEntry(date: future), "future"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithUnknownEntryType_Returns400()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var response = await client.SendAsync(
            Post("/v1/entries", new { type = "Refund", amount = 10m, entryDate = Today }, "bad-type"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Reverse_CreatesCompensatingEntry_AndSecondReversalIsConflict()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;
        var created = await ReadAsync(await client.SendAsync(Post("/v1/entries", NewEntry(50m), "to-reverse")));
        var id = created.GetProperty("id").GetGuid();

        var first = await client.SendAsync(Post($"/v1/entries/{id}/reversals", new { reason = "Wrong value" }, "reverse-1"));
        var second = await client.SendAsync(Post($"/v1/entries/{id}/reversals", new { reason = "Again" }, "reverse-2"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var reversal = await ReadAsync(first);
        Assert.Equal("Debit", reversal.GetProperty("type").GetString());
        Assert.Equal(id, reversal.GetProperty("reversalOf").GetGuid());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var original = await ReadAsync(await client.SendAsync(Get($"/v1/entries/{id}")));
        Assert.Equal(reversal.GetProperty("id").GetGuid(), original.GetProperty("reversedBy").GetGuid());
    }

    [Fact]
    public async Task Reverse_UnknownOrOtherMerchantsEntry_Returns404()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;
        var created = await ReadAsync(await client.SendAsync(Post("/v1/entries", NewEntry(), "mine")));
        var id = created.GetProperty("id").GetGuid();

        var unknown = await client.SendAsync(Post($"/v1/entries/{Guid.NewGuid()}/reversals", null, "r1"));
        var foreign = await client.SendAsync(Post($"/v1/entries/{id}/reversals", null, "r2", OtherMerchant));
        var foreignRead = await client.SendAsync(Get($"/v1/entries/{id}", OtherMerchant));

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
    }

    [Fact]
    public async Task List_FiltersByMerchantAndPeriod_AndPaginates()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;
        var today = MerchantCalendar.Today(DateTimeOffset.UtcNow);
        for (var i = 0; i < 5; i++)
        {
            var date = today.AddDays(-i).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            await client.SendAsync(Post("/v1/entries", NewEntry(10 + i, date), $"list-{i}"));
        }

        await client.SendAsync(Post("/v1/entries", NewEntry(999m), "other", OtherMerchant));

        var from = today.AddDays(-3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var page1 = await ReadAsync(await client.SendAsync(Get($"/v1/entries?from={from}&to={Today}&page=1&pageSize=3")));
        var page2 = await ReadAsync(await client.SendAsync(Get($"/v1/entries?from={from}&to={Today}&page=2&pageSize=3")));

        Assert.Equal(4, page1.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, page1.GetProperty("items").GetArrayLength());
        Assert.Equal(1, page2.GetProperty("items").GetArrayLength());
        Assert.Equal(10m, page1.GetProperty("items")[0].GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task List_WithPeriodAboveOneYear_Returns400()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var response = await client.SendAsync(Get("/v1/entries?from=2024-01-01&to=2026-01-01"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Health_ReturnsHealthy()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode); // checks the database
    }

    private static HttpRequestMessage AsAuditor(string url, string scopes, IEnumerable<Guid>? assigned, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url);
        request.Headers.Authorization = TestAuth.Bearer(null, scopes, subject: "auditor-1", auditMerchants: assigned);

        return request;
    }

    [Fact]
    public async Task AnAuditor_ReadsTheEntriesOfAnAssignedMerchant_AndOnlyThose()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;
        var created = await ReadAsync(await client.SendAsync(Post("/v1/entries", NewEntry(42m), "aud-1")));
        var id = created.GetProperty("id").GetGuid();
        await client.SendAsync(Post("/v1/entries", NewEntry(7m), "aud-2", OtherMerchant));

        var list = await client.SendAsync(AsAuditor($"/v1/entries?merchantId={Merchant}&from={Today}&to={Today}", "entries:audit", [Merchant]));
        var one = await client.SendAsync(AsAuditor($"/v1/entries/{id}?merchantId={Merchant}", "entries:audit", [Merchant]));

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(42m, (await ReadAsync(list)).GetProperty("items")[0].GetProperty("amount").GetDecimal());
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);

        // Not assigned: refused. No merchantId: the auditor must say whose data it wants.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(AsAuditor($"/v1/entries?merchantId={OtherMerchant}", "entries:audit", [Merchant]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(AsAuditor("/v1/entries", "entries:audit", [Merchant]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(AsAuditor($"/v1/entries?merchantId={Merchant}", "entries:audit", []))).StatusCode);
    }

    [Fact]
    public async Task AnAuditor_IsReadOnly_EvenWhenAskingToWrite()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;
        var created = await ReadAsync(await client.SendAsync(Post("/v1/entries", NewEntry(10m), "aud-w")));
        var id = created.GetProperty("id").GetGuid();

        var register = AsAuditor("/v1/entries", "entries:audit entries:write", [Merchant], HttpMethod.Post);
        register.Content = JsonContent.Create(NewEntry(1m));
        register.Headers.Add("Idempotency-Key", "aud-write");
        var reverse = AsAuditor($"/v1/entries/{id}/reversals", "entries:audit entries:write", [Merchant], HttpMethod.Post);
        reverse.Headers.Add("Idempotency-Key", "aud-reverse");

        // Even holding a write scope, a token with no merchant of its own cannot write: every write needs one.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(register)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(reverse)).StatusCode);
    }

    [Fact]
    public async Task AMerchant_CannotReadAnotherMerchant_ByNamingIt()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;
        await client.SendAsync(Post("/v1/entries", NewEntry(7m), "aud-3", OtherMerchant));

        var response = await client.SendAsync(Get($"/v1/entries?merchantId={OtherMerchant}&from={Today}&to={Today}"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); // refused, not answered with its own (empty) list
    }

    [Fact]
    public async Task TheAuditScopeOfAnotherResource_AndTheMerchantScopes_DoNotOpenTheAuditPath()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        // balances:audit is the other service's scope; entries:read without a merchant has nothing to read.
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(AsAuditor($"/v1/entries?merchantId={Merchant}", "balances:audit", [Merchant]))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(AsAuditor($"/v1/entries?merchantId={Merchant}", "entries:read", [Merchant]))).StatusCode);
    }

    [Fact]
    public async Task AuditVerification_IsForTheJobScopeOnly_AndReportsAnIntactChain()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;
        await client.SendAsync(Post("/v1/entries", NewEntry(5m), "audit-1"));

        var asMerchant = new HttpRequestMessage(HttpMethod.Get, "/v1/audit/verify");
        asMerchant.Headers.Authorization = TestAuth.Bearer(Merchant, "entries:read entries:write");
        var asJob = new HttpRequestMessage(HttpMethod.Get, "/v1/audit/verify");
        asJob.Headers.Authorization = TestAuth.Bearer(null, "entries:reconcile");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(asMerchant)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/audit/verify")).StatusCode);
        var report = await ReadAsync(await client.SendAsync(asJob));
        Assert.True(report.GetProperty("isIntact").GetBoolean());
        Assert.Equal(1, report.GetProperty("links").GetInt64());
    }

    [Fact]
    public async Task ARepeatedRequest_DoesNotAddASecondLinkToTheChain()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        await client.SendAsync(Post("/v1/entries", NewEntry(5m), "audit-same"));
        await client.SendAsync(Post("/v1/entries", NewEntry(5m), "audit-same")); // replayed by the idempotency key

        var verify = new HttpRequestMessage(HttpMethod.Get, "/v1/audit/verify");
        verify.Headers.Authorization = TestAuth.Bearer(null, "entries:reconcile");
        Assert.Equal(1, (await ReadAsync(await client.SendAsync(verify))).GetProperty("links").GetInt64());
    }
}
