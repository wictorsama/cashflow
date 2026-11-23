using CashFlow.Entries.Application;
using CashFlow.Entries.Domain;
using CashFlow.Entries.Infrastructure.Persistence;
using CashFlow.Security;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CashFlow.Entries.Api;

/// <summary>Turns known failures into RFC 9457 problem responses. Anything else becomes a generic 500.</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    private const string ReversalUniqueIndex = "IX_entries_reversal_of";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            MerchantClaimMissingException or AccessDeniedException => (StatusCodes.Status403Forbidden, "Forbidden"),
            MerchantRequiredException => (StatusCodes.Status400BadRequest, "Invalid request"),
            InvalidRequestException or BadHttpRequestException => (StatusCodes.Status400BadRequest, "Invalid request"),
            EntryNotFoundException => (StatusCodes.Status404NotFound, "Entry not found"),
            EntryAlreadyReversedException => (StatusCodes.Status409Conflict, "Entry already reversed"),
            DbUpdateException { InnerException: PostgresException { ConstraintName: ReversalUniqueIndex } } =>
                (StatusCodes.Status409Conflict, "Entry already reversed"),
            DomainException => (StatusCodes.Status422UnprocessableEntity, "Business rule violated"),
            IdempotencyKeyReusedException => (StatusCodes.Status422UnprocessableEntity, "Idempotency-Key reused"),
            _ => (0, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;

        // Never leak database details: concurrent-reversal conflicts get the same text as the sequential case.
        var detail = exception switch
        {
            DbUpdateException => "The entry was already reversed.",
            BadHttpRequestException => "The request body or parameters are malformed.",
            _ => exception.Message,
        };

        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        });
    }
}
