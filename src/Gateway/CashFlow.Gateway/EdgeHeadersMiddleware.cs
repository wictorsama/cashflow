using System.Text.RegularExpressions;

namespace CashFlow.Gateway;

/// <summary>
/// Gives every request a correlation id (kept when the caller sent a sane one, generated otherwise), forwards it to the
/// services, returns it to the caller and adds basic security headers to every response.
/// </summary>
public sealed partial class EdgeHeadersMiddleware(RequestDelegate next, ILogger<EdgeHeadersMiddleware> logger)
{
    public const string CorrelationHeader = "X-Correlation-Id";

    /// <summary>Entries are tiny; a large body is never legitimate.</summary>
    public const long MaxBodyBytes = 64 * 1024;

    // Only a conservative alphabet: the value ends up in logs and headers, so it must not carry injected content.
    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex ValidCorrelationId();

    public async Task InvokeAsync(HttpContext context)
    {
        var supplied = context.Request.Headers[CorrelationHeader].ToString();
        var correlationId = ValidCorrelationId().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString("N");

        context.Request.Headers[CorrelationHeader] = correlationId; // forwarded by the proxy
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers[CorrelationHeader] = correlationId;
            headers.Remove("Server"); // the proxied service would otherwise disclose its server software
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Cache-Control"] = "no-store";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";

            return Task.CompletedTask;
        });

        // Fail fast on an oversized declared body. Kestrel enforces the same limit for chunked bodies.
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return;
        }

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = RemoveLineBreaks(correlationId) }))
        {
            await next(context);
        }
    }

    /// <summary>
    /// The id is either generated here or matched the strict pattern above (letters, digits and hyphens), so it already
    /// has no line breaks; they are removed again anyway so that nothing a caller sent can ever forge a log line.
    /// </summary>
    private static string RemoveLineBreaks(string value) =>
        value.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal);
}
