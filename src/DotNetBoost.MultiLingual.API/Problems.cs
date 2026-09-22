using DotNetBoost.MultiLingual.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace DotNetBoost.MultiLingual.API;

/// <summary>Maps <see cref="MultiLingualException"/> subtypes onto RFC 7807 problem details.</summary>
public static class MultiLingualProblems
{
    /// <summary>The HTTP status for <paramref name="exception"/>.</summary>
    public static int StatusCodeFor(MultiLingualException exception) => exception switch
    {
        // Before its base type: the request used a language that does not exist — its fault, 400.
        UnknownLanguageException     => StatusCodes.Status400BadRequest,
        LanguageNotFoundException    => StatusCodes.Status404NotFound,
        InvalidLanguageException     => StatusCodes.Status400BadRequest,
        LanguageConflictException    => StatusCodes.Status409Conflict,
        NoLanguageConfiguredException => StatusCodes.Status409Conflict,
        BaseLanguageRequiredException => StatusCodes.Status409Conflict,
        BaseLanguageNotTranslatableException => StatusCodes.Status409Conflict,
        _                            => StatusCodes.Status400BadRequest
    };

    /// <summary>
    /// Problem details describing <paramref name="exception"/>. With <paramref name="httpContext"/>
    /// and an <see cref="IStringLocalizer{T}"/> registered — <c>AddJsonLocalization()</c> — the title
    /// and detail are looked up in the request's language under
    /// <c>MultiLingual.Problems.{Kind}.Title</c> and <c>.Detail</c>, where Kind is the exception's
    /// type name without "Exception". A key that is not there keeps the built-in English text.
    /// Details take the exception's data as arguments:
    /// <list type="bullet">
    ///   <item><c>UnknownLanguage.Detail</c>: <c>{0}</c> code, <c>{1}</c> what it was used for (English).</item>
    ///   <item><c>LanguageNotFound.Detail</c>: <c>{0}</c> code.</item>
    ///   <item><c>NoLanguageConfigured.Detail</c>: none.</item>
    ///   <item><c>BaseLanguageRequired.Detail</c>: <c>{0}</c> entity, <c>{1}</c> base language, <c>{2}</c> requested language.</item>
    ///   <item><c>BaseLanguageNotTranslatable.Detail</c>: <c>{0}</c> base language, <c>{1}</c> entity.</item>
    /// </list>
    /// The other exceptions describe many different rules in one type, so only their title is
    /// localizable; the detail stays the exception's message.
    /// </summary>
    public static ProblemDetails ToProblemDetails(MultiLingualException exception, HttpContext? httpContext = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var status    = StatusCodeFor(exception);
        var localizer = httpContext?.RequestServices?.GetService<IStringLocalizer<MultiLingualMessages>>();
        var prefix    = $"MultiLingual.Problems.{KindOf(exception)}";

        var title = exception switch
        {
            UnknownLanguageException     => "Unknown language",
            LanguageNotFoundException    => "Language not found",
            InvalidLanguageException     => "Invalid language",
            BaseLanguageRequiredException => "Base language required",
            BaseLanguageNotTranslatableException => "Base language is not a translation",
            _                            => "Conflict"
        };

        return new ProblemDetails
        {
            Status   = status,
            Title    = Localize(localizer, $"{prefix}.Title", title, []),
            Detail   = DetailArguments(exception) is { } arguments
                ? Localize(localizer, $"{prefix}.Detail", exception.Message, arguments)
                : exception.Message,
            Instance = httpContext?.Request.Path
        };
    }

    private static string KindOf(MultiLingualException exception)
    {
        var name = exception.GetType().Name;
        return name.EndsWith("Exception", StringComparison.Ordinal) ? name[..^"Exception".Length] : name;
    }

    private static object[]? DetailArguments(MultiLingualException exception) => exception switch
    {
        UnknownLanguageException e      => [e.Code, e.Usage],
        LanguageNotFoundException e     => [e.Code],
        NoLanguageConfiguredException   => [],
        BaseLanguageRequiredException e => [e.EntityName, e.BaseLanguage, e.RequestedLanguage],
        BaseLanguageNotTranslatableException e => [e.BaseLanguage, e.EntityName],
        _                               => null
    };

    private static string Localize(IStringLocalizer? localizer, string key, string fallback, object[] arguments)
    {
        if (localizer is null)
            return fallback;

        var message = localizer[key, arguments];
        return message.ResourceNotFound ? fallback : message.Value;
    }
}

/// <summary>Names the library's own messages for <see cref="IStringLocalizer{T}"/>.</summary>
internal sealed class MultiLingualMessages;

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
