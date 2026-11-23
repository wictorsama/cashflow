using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CashFlow.Entries.Application;
using CashFlow.Entries.Infrastructure.Persistence;
using CashFlow.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CashFlow.Entries.Api;

public static class EntryEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";
    private const int MaxIdempotencyKeyLength = 100;

    public static void MapEntryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/entries");

        group.MapPost("/", RegisterAsync).RequireAuthorization(Scopes.EntriesWrite);
        group.MapPost("/{id:guid}/reversals", ReverseAsync).RequireAuthorization(Scopes.EntriesWrite);
        group.MapGet("/", ListAsync).RequireAuthorization(Scopes.EntriesReadOrAudit);
        group.MapGet("/{id:guid}", GetAsync).RequireAuthorization(Scopes.EntriesReadOrAudit);
    }

    private static async Task<IResult> RegisterAsync(
        HttpContext http,
        RegisterEntryRequest request,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        RegisterEntryHandler handler,
        IdempotencyService idempotency,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        CancellationToken cancellationToken)
    {
        var merchantId = MerchantContext.Require(http);
        var key = RequireKey(idempotencyKey);
        var options = json.Value.SerializerOptions;

        var result = await idempotency.ExecuteAsync(
            merchantId,
            key,
            Fingerprint("register", request, options),
            async () =>
            {
                var entry = await handler.HandleAsync(
                    new RegisterEntryCommand(merchantId, MerchantContext.RequireActor(http), request.Type, request.Amount, request.EntryDate, request.Description),
                    cancellationToken);

                return (StatusCodes.Status201Created, JsonSerializer.Serialize(entry, options));
            },
            cancellationToken);

        return ToResult(http, result);
    }

    private static async Task<IResult> ReverseAsync(
        HttpContext http,
        Guid id,
        ReverseEntryRequest? request,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        ReverseEntryHandler handler,
        IdempotencyService idempotency,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        CancellationToken cancellationToken)
    {
        var merchantId = MerchantContext.Require(http);
        var key = RequireKey(idempotencyKey);
        var options = json.Value.SerializerOptions;
        request ??= new ReverseEntryRequest(null);

        var result = await idempotency.ExecuteAsync(
            merchantId,
            key,
            Fingerprint($"reverse:{id}", request, options),
            async () =>
            {
                var reversal = await handler.HandleAsync(new ReverseEntryCommand(merchantId, MerchantContext.RequireActor(http), id, request.Reason), cancellationToken);

                return (StatusCodes.Status201Created, JsonSerializer.Serialize(reversal, options));
            },
            cancellationToken);

        return ToResult(http, result);
    }

    private static async Task<IResult> GetAsync(
        HttpContext http, Guid id, IEntryQueries queries, [FromQuery] Guid? merchantId, CancellationToken cancellationToken)
    {
        var access = http.ResolveRead(merchantId, Scopes.EntriesAudit, "entry");
        var entry = await queries.GetAsync(access.MerchantId, id, cancellationToken);

        return entry is null ? throw new EntryNotFoundException(id) : Results.Ok(entry);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        ListEntriesHandler handler,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] Guid? merchantId,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var access = http.ResolveRead(merchantId, Scopes.EntriesAudit, "entries");
        var today = MerchantCalendar.Today(clock.GetUtcNow());

        var result = await handler.HandleAsync(
            new ListEntriesQuery(access.MerchantId, from ?? today, to ?? from ?? today, page ?? 1, pageSize ?? 50),
            cancellationToken);

        return Results.Ok(result);
    }

    private static string RequireKey(string? key) =>
        string.IsNullOrWhiteSpace(key) || key.Length > MaxIdempotencyKeyLength
            ? throw new InvalidRequestException(
                $"Header {IdempotencyHeader} is required (up to {MaxIdempotencyKeyLength} characters).")
            : key;

    private static string Fingerprint<T>(string operation, T request, JsonSerializerOptions options)
    {
        var bytes = Encoding.UTF8.GetBytes(operation + "|" + JsonSerializer.Serialize(request, options));

        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static IResult ToResult(HttpContext http, IdempotentResponse result)
    {
        if (result.Replayed)
        {
            http.Response.Headers["Idempotent-Replayed"] = "true";
        }

        return Results.Content(result.Body, "application/json", Encoding.UTF8, result.StatusCode);
    }
}
