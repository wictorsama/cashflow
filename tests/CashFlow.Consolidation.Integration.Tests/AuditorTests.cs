using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CashFlow.Consolidation.Application;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CashFlow.Consolidation.Integration.Tests;

/// <summary>An auditor reads the balances of the merchants assigned to it, and nothing else.</summary>
[Collection(SharedInfrastructure.Name)]
public sealed class AuditorTests(InfrastructureFixture infra)
{
    private static readonly Guid MerchantA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid MerchantB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<(WebApplicationFactory<Program> Factory, HttpClient Client)> StartAsync()
    {
        var connection = await infra.CreateConsolidationDatabaseAsync();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", connection);
            builder.UseSetting("RabbitMq:Uri", "amqp://nobody:nobody@127.0.0.1:1/");
            builder.UseSetting("Redis:ConnectionString", infra.RedisConnectionString);
            TestAuth.Configure(builder);
        });

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var applier = scope.ServiceProvider.GetRequiredService<EntryEventApplier>();
            await applier.HandleAsync(Event(MerchantA, 100m), default);
            await applier.HandleAsync(Event(MerchantB, 7m), default);
        }

        return (factory, factory.CreateClient());
    }

    private static byte[] Event(Guid merchant, decimal amount) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new EntryEvent(Guid.NewGuid(), "EntryRegistered", 1, DateTimeOffset.UtcNow, merchant, Guid.NewGuid(), "Credit", amount, Day, null),
            JsonOptions);

    private static HttpRequestMessage Get(string url, Guid? merchant, string scopes, params Guid[]? assigned)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = TestAuth.Bearer(merchant, scopes, assigned);

        return request;
    }

    [Fact]
    public async Task AnAuditor_ReadsTheBalanceOfAnAssignedMerchant_DayAndRange()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        var day = await client.SendAsync(Get($"/v1/balances/2026-11-10?merchantId={MerchantA}", null, "balances:audit", MerchantA));
        var range = await client.SendAsync(Get($"/v1/balances?merchantId={MerchantB}&from=2026-11-10&to=2026-11-10", null, "balances:audit", MerchantA, MerchantB));

        Assert.Equal(HttpStatusCode.OK, day.StatusCode);
        Assert.Equal(100m, (await day.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credits").GetDecimal());
        Assert.Equal(HttpStatusCode.OK, range.StatusCode);
        Assert.Equal(7m, (await range.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("days")[0].GetProperty("credits").GetDecimal());
    }

    [Fact]
    public async Task AnAuditor_CannotReadAnUnassignedMerchant_OrOmitTheMerchant_OrHoldNoAssignment()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get($"/v1/balances/2026-11-10?merchantId={MerchantB}", null, "balances:audit", MerchantA))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Get("/v1/balances/2026-11-10", null, "balances:audit", MerchantA))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get($"/v1/balances/2026-11-10?merchantId={MerchantA}", null, "balances:audit"))).StatusCode);
    }

    [Fact]
    public async Task AnAuditor_CannotRebuildOrReconcile()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;
        var rebuild = new HttpRequestMessage(HttpMethod.Post, "/v1/balances/rebuild") { Content = JsonContent.Create(new { merchantId = MerchantA, from = "2026-11-10", to = "2026-11-10" }) };
        rebuild.Headers.Authorization = TestAuth.Bearer(null, "balances:audit", [MerchantA]);
        var reconcile = new HttpRequestMessage(HttpMethod.Post, "/v1/balances/reconcile") { Content = JsonContent.Create(new { }) };
        reconcile.Headers.Authorization = TestAuth.Bearer(null, "balances:audit", [MerchantA]);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(rebuild)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(reconcile)).StatusCode);
    }

    [Fact]
    public async Task AMerchant_CannotReadAnotherMerchant_ByNamingIt_AndAnEntriesAuditScopeDoesNotOpenBalances()
    {
        var (factory, client) = await StartAsync();
        await using var _ = factory;

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get($"/v1/balances/2026-11-10?merchantId={MerchantB}", MerchantA, "balances:read"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get($"/v1/balances/2026-11-10?merchantId={MerchantA}", MerchantA, "balances:read"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Get($"/v1/balances/2026-11-10?merchantId={MerchantA}", null, "entries:audit", MerchantA))).StatusCode);
    }
}
