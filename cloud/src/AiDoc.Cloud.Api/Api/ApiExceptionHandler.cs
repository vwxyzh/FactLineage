using AiDoc.Cloud.Api.Domain;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AiDoc.Cloud.Api.Api;

public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, code) = exception switch
        {
            DomainException domain when domain.Code.EndsWith("_NOT_FOUND", StringComparison.Ordinal) =>
                (StatusCodes.Status404NotFound, domain.Message, domain.Code),
            DomainException domain when domain.Code == "PROJECT_ALREADY_EXISTS" =>
                (StatusCodes.Status409Conflict, domain.Message, domain.Code),
            DomainException domain =>
                (StatusCodes.Status400BadRequest, domain.Message, domain.Code),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.", "INTERNAL_ERROR")
        };
        if (status == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled request failure.");
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Extensions = { ["code"] = code }
            },
            Exception = exception
        });
    }
}