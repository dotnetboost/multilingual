using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;

namespace SampleApp.Infrastructure;

/// <summary>
/// Names the sample's messages for <see cref="IStringLocalizer{T}"/>. With the JSON localizer
/// every localizer reads the same files (Resources/en.json, Resources/ar.json), so this is just
/// a type to put in the brackets.
/// </summary>
public sealed class SharedResource;

/// <summary>
/// Base for expected, caller-facing failures that map to a specific status code. It carries a
/// message KEY and its arguments rather than text: <see cref="AppExceptionHandler"/> looks the
/// key up in the caller's language, so controllers never touch the localizer for errors, and
/// the logs keep the same key whichever language the caller asked for.
/// </summary>
public abstract class AppException(string messageKey, params object[] arguments) : Exception(messageKey)
{
    /// <summary>The message key, e.g. <c>Products.NotFound</c>.</summary>
    public string MessageKey { get; } = messageKey;

    /// <summary>Values for the message's <c>{0}</c>, <c>{1}</c>, … placeholders.</summary>
    public IReadOnlyList<object> Arguments { get; } = arguments;

    public abstract int StatusCode { get; }

    /// <summary>The key of the problem's title, e.g. <c>Errors.NotFound</c>.</summary>
    public abstract string TitleKey { get; }
}

/// <summary>404 — the addressed resource does not exist.</summary>
public sealed class NotFoundException(string messageKey, params object[] arguments) : AppException(messageKey, arguments)
{
    public override int StatusCode => StatusCodes.Status404NotFound;

    public override string TitleKey => "Errors.NotFound";
}

/// <summary>400 — the request is well-formed but its content is not acceptable.</summary>
public sealed class ValidationException(string messageKey, params object[] arguments) : AppException(messageKey, arguments)
{
    public override int StatusCode => StatusCodes.Status400BadRequest;

    public override string TitleKey => "Errors.Invalid";
}

/// <summary>409 — the request conflicts with a rule that protects existing state.</summary>
public sealed class ConflictException(string messageKey, params object[] arguments) : AppException(messageKey, arguments)
{
    public override int StatusCode => StatusCodes.Status409Conflict;

    public override string TitleKey => "Errors.Conflict";
}

/// <summary>
/// Turns <see cref="AppException"/> into RFC 7807 responses in the caller's language;
/// everything else into a 500.
/// </summary>
public sealed partial class AppExceptionHandler(ILogger<AppExceptionHandler> logger) : IExceptionHandler
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}.")]
    private partial void LogUnhandled(Exception exception, string method, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Status} {MessageKey}")]
    private partial void LogRejected(int status, string messageKey);

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // Exception handlers are singletons, so the localizer comes from the request's services:
        // that is the one that knows the request's language. Injected through the constructor it
        // would come from the root, which has none.
        var localizer = httpContext.RequestServices.GetRequiredService<IStringLocalizer<SharedResource>>();

        ProblemDetails problem;
        if (exception is AppException app)
        {
            LogRejected(app.StatusCode, app.MessageKey);

            problem = new ProblemDetails
            {
                Status = app.StatusCode,
                Title = localizer[app.TitleKey],
                Detail = localizer[app.MessageKey, [.. app.Arguments]]
            };
        }
        else
        {
            LogUnhandled(exception, httpContext.Request.Method, httpContext.Request.Path);

            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = localizer["Errors.Unexpected"],
                Detail = localizer["Errors.UnexpectedDetail"]
            };
        }

        problem.Instance = httpContext.Request.Path;
        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
