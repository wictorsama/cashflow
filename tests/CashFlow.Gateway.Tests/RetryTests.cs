using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CashFlow.Gateway.Tests;

/// <summary>The gateway in front of a service that fails a scripted number of times before it answers.</summary>
public sealed class RetryTests : IAsyncLifetime
{
    private static readonly Guid Merchant = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private WebApplication _service = null!;
    private string _address = null!;
    private int _calls;

    /// <summary>How the service answers each call, 1-based; calls beyond the list get the last answer.</summary>
    private List<Func<HttpContext, Task>> Script { get; } = [];

    private List<string> Bodies { get; } = [];

    private List<string?> Keys { get; } = [];

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
        _service = builder.Build();
        _service.Map("/{**path}", async (HttpContext http) =>
        {
            int call;
            lock (Bodies)
            {
                call = ++_calls;
            }

            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            lock (Bodies)
            {
                Bodies.Add(body);
                Keys.Add(http.Request.Headers["Idempotency-Key"]);
            }

            await Script[Math.Min(call, Script.Count) - 1](http);
        });

        await _service.StartAsync();
        _address = _service.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First() + "/";
    }

    public async Task DisposeAsync() => await _service.DisposeAsync();

    private static Func<HttpContext, Task> Status(int code) => http =>
    {
        http.Response.StatusCode = code;
        return Task.CompletedTask;
    };

    private static Func<HttpContext, Task> Ok(string body = """{"ok":true}""") => async http =>
    {
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync(body);
    };

    private WebApplicationFactory<Program> StartGateway(int maxAttempts = 3, string? activityTimeout = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            TestAuth.Configure(builder);
            builder.UseSetting("Retry:MaxAttempts", maxAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Retry:DelayMilliseconds", "5");
            builder.UseSetting("ReverseProxy:Clusters:entries:Destinations:primary:Address", _address);
            builder.UseSetting("ReverseProxy:Clusters:consolidation:Destinations:primary:Address", _address);
            if (activityTimeout is not null)
            {
                builder.UseSetting("ReverseProxy:Clusters:entries:HttpRequest:ActivityTimeout", activityTimeout);
            }
        });

    private static HttpRequestMessage Request(HttpMethod method, string url, string? body = null, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestAuth.Token(Merchant, "entries:read entries:write balances:read"));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    [Fact]
    public async Task ARead_ThatGetsUnavailableTwice_IsRepeated_AndTheCallerSeesTheAnswer()
    {
        Script.AddRange([Status(503), Status(503), Ok()]);
        await using var factory = StartGateway();

        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Get, "/v1/balances/2026-11-17"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"ok":true}""", await response.Content.ReadAsStringAsync());
        Assert.Equal(3, _calls);
        Assert.Matches("^[0-9a-f]{32}$", response.Headers.GetValues("X-Correlation-Id").Single()); // headers still added once
    }

    [Fact]
    public async Task AWriteWithAnIdempotencyKey_IsRepeated_WithTheSameBodyAndKeyOnEveryTry()
    {
        Script.AddRange([Status(504), Ok("""{"id":"a"}""")]);
        await using var factory = StartGateway();

        var response = await factory.CreateClient().SendAsync(
            Request(HttpMethod.Post, "/v1/entries", body: """{"amount":10.50,"type":"credit"}""", idempotencyKey: "key-12345678"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _calls);
        Assert.All(Bodies, b => Assert.Equal("""{"amount":10.50,"type":"credit"}""", b)); // the body is sent again, not lost
        Assert.All(Keys, k => Assert.Equal("key-12345678", k));
    }

    [Fact]
    public async Task AWriteWithoutAnIdempotencyKey_IsNeverRepeated()
    {
        Script.AddRange([Status(503), Ok()]);
        await using var factory = StartGateway();

        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Post, "/v1/entries", body: """{"amount":1}"""));

        // Without the key a second try could register the entry twice.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task WhenEveryTryFails_TheLastAnswerIsReturned_AfterTheConfiguredAttempts()
    {
        Script.Add(Status(503));
        await using var factory = StartGateway(maxAttempts: 3);

        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Get, "/v1/entries"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(3, _calls);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(422)]
    [InlineData(500)]
    public async Task AnswersThatAreNotAboutAvailability_AreReturnedAtOnce(int code)
    {
        Script.AddRange([Status(code), Ok()]);
        await using var factory = StartGateway();

        var response = await factory.CreateClient().SendAsync(
            Request(HttpMethod.Post, "/v1/entries", body: """{"amount":1}""", idempotencyKey: "key-12345678"));

        Assert.Equal(code, (int)response.StatusCode);
        Assert.Equal(1, _calls); // a validation error or a conflict would only repeat itself
    }

    [Fact]
    public async Task AServiceThatStopsAnswering_IsTimedOut_AndTheNextTryReachesAHealthyOne()
    {
        Script.AddRange([async http => await Task.Delay(TimeSpan.FromSeconds(3), http.RequestAborted), Ok()]);
        await using var factory = StartGateway(activityTimeout: "00:00:01");

        var response = await factory.CreateClient().SendAsync(
            Request(HttpMethod.Post, "/v1/entries", body: """{"amount":1}""", idempotencyKey: "key-12345678"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, _calls);
    }

    [Fact]
    public async Task WithRetriesTurnedOff_NothingIsRepeated()
    {
        Script.AddRange([Status(503), Ok()]);
        await using var factory = StartGateway(maxAttempts: 1);

        var response = await factory.CreateClient().SendAsync(Request(HttpMethod.Get, "/v1/entries"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, _calls);
    }
}
