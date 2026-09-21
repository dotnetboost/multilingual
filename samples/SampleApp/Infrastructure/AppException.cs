using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace SampleApp.Infrastructure;

/// <summary>Base for expected, caller-facing failures that map to a specific status code.</summary>
public abstract class AppException(string message) : Exception(message)
{
    public abstract int StatusCode { get; }

    public abstract string Title { get; }
}

/// <summary>404 — the addressed resource does not exist.</summary>
public sealed class NotFoundException(string message) : AppException(message)
{
    public override int StatusCode => StatusCodes.Status404NotFound;

    public override string Title => "Resource not found";
}

/// <summary>400 — the request is well-formed but its content is not acceptable.</summary>
public sealed class ValidationException(string message) : AppException(message)
{
    public override int StatusCode => StatusCodes.Status400BadRequest;

    public override string Title => "Invalid request";
}

/// <summary>409 — the request conflicts with a rule that protects existing state.</summary>
public sealed class ConflictException(string message) : AppException(message)
{
    public override int StatusCode => StatusCodes.Status409Conflict;

    public override string Title => "Conflict";
}

/// <summary>Turns <see cref="AppException"/> into RFC 7807 responses; everything else into a 500.</summary>
public sealed partial class AppExceptionHandler(ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}.")]
    private partial void LogUnhandled(Exception exception, string method, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Title}: {Detail}")]
    private partial void LogRejected(string? title, string? detail);

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var problem = exception switch
        {
            AppException app => new ProblemDetails
            {
                Status = app.StatusCode,
                Title = app.Title,
                Detail = app.Message
            },
            _ => null
        };

        if (problem is null)
        {
            LogUnhandled(exception, httpContext.Request.Method, httpContext.Request.Path);

            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "Unexpected error",
                Detail = "An unexpected error occurred while processing the request."
            };
        }
        else
        {
            LogRejected(problem.Title, problem.Detail);
        }

        problem.Instance = httpContext.Request.Path;
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
