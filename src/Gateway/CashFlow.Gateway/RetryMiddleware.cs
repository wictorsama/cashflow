using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;

namespace CashFlow.Gateway;

public sealed class RetryOptions
{
    /// <summary>Total tries, the first one included. 1 turns the retry off.</summary>
    public int MaxAttempts { get; set; } = 4;

    /// <summary>Wait before the second try; it grows linearly with each further one.</summary>
    public int DelayMilliseconds { get; set; } = 250;
}

/// <summary>
/// Tries again when the gateway could not get an answer from a service (it was unreachable, timed out, or said it is
/// unavailable): 502, 503 or 504. The next try goes through the Kubernetes Service again, so it usually reaches
/// another replica. This is what hides a replica or a database failing over from the caller.
///
/// It only repeats requests that are safe to repeat:
///  - reads (GET, HEAD), and
///  - writes that carry an Idempotency-Key, because the service answers a repeated key with the original result
///    instead of registering a second entry. That matters when the first try did reach the database and only the
///    answer was lost.
/// A write without a key is never repeated. Any other answer, including 4xx and 500, is returned as it is.
/// </summary>
public sealed partial class RetryMiddleware(RequestDelegate next, IOptions<RetryOptions> options, ILogger<RetryMiddleware> logger)
{
    public const string IdempotencyHeader = "Idempotency-Key";

    private static readonly Meter Meter = new("CashFlow.Gateway");
    private static readonly Counter<long> Retries = Meter.CreateCounter<long>(
        "cashflow.gateway.retries", description: "Requests that needed more than one try, by how they ended.");

    private readonly RetryOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        if (_options.MaxAttempts <= 1 || !IsSafeToRepeat(context.Request))
        {
            await next(context);
            return;
        }

        // The body is read again on each try. It is at most MaxBodyBytes (checked before this point), so it stays in memory.
        context.Request.EnableBuffering(bufferThreshold: (int)EdgeHeadersMiddleware.MaxBodyBytes + 1, bufferLimit: EdgeHeadersMiddleware.MaxBodyBytes);

        var realBody = context.Response.Body;
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                // The answer is held back until we know it is the final one.
                await using var held = new MemoryStream();
                context.Response.Body = held;
                context.Request.Body.Position = 0;

                await next(context);

                var status = context.Response.StatusCode;
                var failed = status is StatusCodes.Status502BadGateway or StatusCodes.Status503ServiceUnavailable or StatusCodes.Status504GatewayTimeout;
                var canTryAgain = failed
                    && attempt < _options.MaxAttempts
                    && !context.Response.HasStarted
                    && !context.RequestAborted.IsCancellationRequested;

                if (!canTryAgain)
                {
                    if (attempt > 1)
                    {
                        Retries.Add(1, new KeyValuePair<string, object?>("outcome", failed ? "exhausted" : "recovered"));
                    }

                    context.Response.Body = realBody;
                    held.Position = 0;
                    await held.CopyToAsync(realBody, context.RequestAborted);
                    return;
                }

                LogRetry(logger, context.Request.Method, context.Request.Path, status, attempt);
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.Headers.Clear();
                await Task.Delay(_options.DelayMilliseconds * attempt, context.RequestAborted);
            }
        }
        finally
        {
            context.Response.Body = realBody;
        }
    }

    private static bool IsSafeToRepeat(HttpRequest request) =>
        HttpMethods.IsGet(request.Method)
        || HttpMethods.IsHead(request.Method)
        || !string.IsNullOrWhiteSpace(request.Headers[IdempotencyHeader].ToString());

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Method} {Path} got {Status} from the service on try {Attempt}; trying again.")]
    private static partial void LogRetry(ILogger logger, string method, PathString path, int status, int attempt);
}
