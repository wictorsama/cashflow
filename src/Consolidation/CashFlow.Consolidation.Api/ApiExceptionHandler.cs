using CashFlow.Consolidation.Application;
using CashFlow.Security;
using Microsoft.AspNetCore.Diagnostics;

namespace CashFlow.Consolidation.Api;

/// <summary>Turns known failures into RFC 9457 problem responses. Anything else becomes a generic 500.</summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            InvalidRequestException => (StatusCodes.Status400BadRequest, "Invalid request", exception.Message),
            BadHttpRequestException => (StatusCodes.Status400BadRequest, "Invalid request", "The request body or parameters are malformed."),
            MerchantClaimMissingException or AccessDeniedException => (StatusCodes.Status403Forbidden, "Forbidden", exception.Message),
            MerchantRequiredException => (StatusCodes.Status400BadRequest, "Invalid request", exception.Message),
            _ => (0, string.Empty, string.Empty),
        };

        if (status == 0)
        {
            return false;
        }

        httpContext.Response.StatusCode = status;

        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        });
    }
}
