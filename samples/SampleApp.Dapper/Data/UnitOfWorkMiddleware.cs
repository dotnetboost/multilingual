namespace SampleApp.Dapper.Data;

/// <summary>
/// Commits <see cref="UnitOfWork"/> when a request has succeeded, and leaves it alone when it has
/// not — an uncommitted transaction is rolled back as the request scope disposes.
/// <para>
/// Middleware rather than an MVC action filter, deliberately. A filter would cover the controllers
/// and miss <c>MapLanguageEndpoints()</c>, which are minimal APIs: a <c>POST /api/languages</c>
/// would then write its language into a transaction nobody commits, and the language would vanish
/// on the way out with nothing logged. Anything that shares the request's transaction has to share
/// its commit.
/// </para>
/// </summary>
public sealed class UnitOfWorkMiddleware(RequestDelegate next)
{
    /// <summary>Runs the rest of the pipeline, then commits if it produced a success.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // An exception thrown downstream propagates through here, so the commit is skipped and the
        // scope rolls the transaction back. UseExceptionHandler is registered upstream of this, so
        // a failure it turns into ProblemDetails arrives back here as a 4xx/5xx instead — also not
        // a commit.
        await next(context);

        if (context.Response.StatusCode < 400)
        {
            await context.RequestServices.GetRequiredService<UnitOfWork>().CommitAsync(context.RequestAborted);
        }
    }
}
