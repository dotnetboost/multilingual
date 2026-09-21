using DotNetBoost.MultiLingual.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DotNetBoost.MultiLingual.API;

/// <summary>Maps <see cref="MultiLingualException"/> subtypes onto RFC 7807 problem details.</summary>
public static class MultiLingualProblems
{
    /// <summary>The HTTP status for <paramref name="exception"/>.</summary>
    public static int StatusCodeFor(MultiLingualException exception) => exception switch
    {
        LanguageNotFoundException    => StatusCodes.Status404NotFound,
        InvalidLanguageException     => StatusCodes.Status400BadRequest,
        LanguageConflictException    => StatusCodes.Status409Conflict,
        NoLanguageConfiguredException => StatusCodes.Status409Conflict,
        BaseLanguageRequiredException => StatusCodes.Status409Conflict,
        _                            => StatusCodes.Status400BadRequest
    };

    /// <summary>Problem details describing <paramref name="exception"/>.</summary>
    public static ProblemDetails ToProblemDetails(MultiLingualException exception, HttpContext? httpContext = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var status = StatusCodeFor(exception);
        return new ProblemDetails
        {
            Status   = status,
            Title    = exception switch
            {
                LanguageNotFoundException    => "Language not found",
                InvalidLanguageException     => "Invalid language",
                BaseLanguageRequiredException => "Base language required",
                _                            => "Conflict"
            },
            Detail   = exception.Message,
            Instance = httpContext?.Request.Path
        };
    }
}

/// <summary>
/// Answers <see cref="MultiLingualException"/>s thrown anywhere in the application — for
/// example <see cref="NoLanguageConfiguredException"/> from your own endpoints — with problem
/// details. Everything else is left to the next handler. Register it with
/// <c>services.AddExceptionHandler&lt;MultiLingualExceptionHandler&gt;()</c> and <c>app.UseExceptionHandler()</c>.
/// </summary>
/// <param name="problemDetails">Writes the response when <c>AddProblemDetails()</c> is registered.</param>
public sealed class MultiLingualExceptionHandler(IProblemDetailsService? problemDetails = null) : IExceptionHandler
{
    /// <inheritdoc/>
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not MultiLingualException known)
            return false;

        var problem = MultiLingualProblems.ToProblemDetails(known, httpContext);
        httpContext.Response.StatusCode = problem.Status!.Value;

        if (problemDetails is not null
            && await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext    = httpContext,
                ProblemDetails = problem,
                Exception      = exception
            }))
        {
            return true;
        }

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}

/// <summary>Turns <see cref="MultiLingualException"/>s from the language endpoints into problem details.</summary>
internal sealed class MultiLingualProblemFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (MultiLingualException ex)
        {
            var problem = MultiLingualProblems.ToProblemDetails(ex, context.HttpContext);
            return TypedResults.Problem(problem);
        }
    }
}
