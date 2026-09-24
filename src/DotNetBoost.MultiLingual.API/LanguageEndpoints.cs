using DotNetBoost.MultiLingual.API;
using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace DotNetBoost.MultiLingual.API
{
    /// <summary>A language as the endpoints return it.</summary>
    /// <param name="Code">Canonical code, e.g. <c>en</c>, <c>pt-BR</c>.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Direction"><c>ltr</c> or <c>rtl</c> — the values of the HTML <c>dir</c> attribute.</param>
    /// <param name="IsDefault">Whether this is the system default.</param>
    /// <param name="IsActive">Whether content is served in it.</param>
    public sealed record LanguageResponse(string Code, string Name, string Direction, bool IsDefault, bool IsActive)
    {
        /// <summary>Maps a snapshot to its wire shape.</summary>
        public static LanguageResponse From(LanguageInfo language)
        {
            ArgumentNullException.ThrowIfNull(language);
            return new(language.Code, language.Name, TextDirections.ToWire(language.Direction), language.IsDefault, language.IsActive);
        }
    }

    /// <summary>Body of <c>POST /api/languages</c>.</summary>
    public sealed record CreateLanguageRequest
    {
        /// <summary>ISO 639-1 code, optionally with a region: <c>en</c>, <c>ar</c>, <c>pt-BR</c>.</summary>
        public string? Code { get; init; }

        /// <summary>Display name, 1–128 characters.</summary>
        public string? Name { get; init; }

        /// <summary>
        /// <c>ltr</c> or <c>rtl</c>. Omit it to take the direction from the runtime's culture data
        /// for the code (<c>ar</c>, <c>he</c>, <c>fa</c>, <c>ur</c> → <c>rtl</c>).
        /// </summary>
        public string? Direction { get; init; }

        /// <summary>Whether content is served in it. Default: true.</summary>
        public bool IsActive { get; init; } = true;

        /// <summary>
        /// Make this the default immediately. The very first language created is always the
        /// default regardless of this flag.
        /// </summary>
        public bool IsDefault { get; init; }
    }

    /// <summary>Body of <c>PUT /api/languages/{code}</c>. A full replacement; the code cannot change.</summary>
    public sealed record UpdateLanguageRequest
    {
        /// <summary>Display name, 1–128 characters.</summary>
        public string? Name { get; init; }

        /// <summary><c>ltr</c> or <c>rtl</c>. Omitting it re-derives the direction from the code.</summary>
        public string? Direction { get; init; }

        /// <summary>The default language cannot be deactivated; promote another one first.</summary>
        public bool IsActive { get; init; } = true;
    }

    /// <summary>Result of purging a language's translations.</summary>
    /// <param name="Code">The language.</param>
    /// <param name="DeletedTranslations">How many rows were removed.</param>
    public sealed record PurgeTranslationsResponse(string Code, int DeletedTranslations);

    /// <summary>
    /// Marks an endpoint (or controller) as independent of the request language, so the OpenAPI
    /// transformer does not advertise <c>Accept-Language</c> on it. The language endpoints carry
    /// it already.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class LanguageAgnosticAttribute : Attribute;

    internal static class TextDirections
    {
        public static string ToWire(TextDirection direction) => direction == TextDirection.Rtl ? "rtl" : "ltr";

        public static TextDirection? Parse(string? value) => value?.Trim().ToUpperInvariant() switch
        {
            null or "" => null,
            "LTR"      => TextDirection.Ltr,
            "RTL"      => TextDirection.Rtl,
            _          => throw new InvalidLanguageException($"'{value}' is not a text direction. Use 'ltr' or 'rtl'.")
        };
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Maps the language management endpoints.</summary>
    public static class LanguageEndpointRouteBuilderExtensions
    {
        /// <summary>
        /// Maps language administration under <paramref name="prefix"/>:
        /// <code>
        /// GET    {prefix}                         list, default first
        /// GET    {prefix}/{code}                  one language
        /// POST   {prefix}                         create (the first becomes the default)
        /// PUT    {prefix}/{code}                  replace name, direction, active flag
        /// DELETE {prefix}/{code}                  delete (refused for the default, or while it has content)
        /// POST   {prefix}/{code}/set-default      promote to default
        /// DELETE {prefix}/{code}/translations     purge every translation in that language
        /// </code>
        /// <para>
        /// <b>They are anonymous</b>, as every <c>Map…</c> in ASP.NET Core is: a library cannot know
        /// which authorization policies an application has declared, so requiring one here would
        /// either invent a policy name every consumer has to define or fail at startup for any
        /// application that has not configured authentication. Protecting them is the
        /// application's decision. The route group is returned so that it takes one line:
        /// <code>
        /// app.MapLanguageEndpoints().RequireAuthorization("admin");
        /// </code>
        /// They change what every client of the application sees — and
        /// <c>DELETE {prefix}/{code}/translations</c> removes content — so secure them before
        /// production.
        /// </para>
        /// </summary>
        public static RouteGroupBuilder MapLanguageEndpoints(this IEndpointRouteBuilder endpoints, string prefix = "/api/languages")
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

            var basePath = prefix.TrimEnd('/');
            var group = endpoints.MapGroup(basePath)
                .WithTags("Languages")
                .WithMetadata(new LanguageAgnosticAttribute())
                .AddEndpointFilter<MultiLingualProblemFilter>();

            group.MapGet("/", GetAllAsync)
                .WithSummary("Lists every language, default first.");

            group.MapGet("/{code}", GetAsync)
                .WithSummary("Gets one language.")
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapPost("/", (CreateLanguageRequest request, IMultiLingualManager multiLingual, CancellationToken ct)
                    => CreateAsync(request, multiLingual, basePath, ct))
                .WithSummary("Creates a language. The first language created becomes the default.")
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status409Conflict);

            group.MapPut("/{code}", UpdateAsync)
                .WithSummary("Replaces a language's name, direction and active flag.")
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);

            group.MapDelete("/{code}", DeleteAsync)
                .WithSummary("Deletes a language. Refused for the default language and while translations in it exist.")
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);

            group.MapPost("/{code}/set-default", SetDefaultAsync)
                .WithSummary("Promotes a language to default and demotes the previous one, atomically.")
                .ProducesProblem(StatusCodes.Status404NotFound)
                .ProducesProblem(StatusCodes.Status409Conflict);

            group.MapDelete("/{code}/translations", PurgeAsync)
                .WithSummary("Deletes every translation written in a language, across all entities.")
                .ProducesProblem(StatusCodes.Status404NotFound);

            return group;
        }

        private static async Task<Ok<List<LanguageResponse>>> GetAllAsync(IMultiLingualManager multiLingual, CancellationToken ct) =>
            TypedResults.Ok((await multiLingual.Languages.GetAllAsync(ct)).Select(LanguageResponse.From).ToList());

        private static async Task<Ok<LanguageResponse>> GetAsync(string code, IMultiLingualManager multiLingual, CancellationToken ct) =>
            TypedResults.Ok(LanguageResponse.From(await multiLingual.Languages.GetAsync(code, ct)));

        private static async Task<Created<LanguageResponse>> CreateAsync(
            CreateLanguageRequest request, IMultiLingualManager multiLingual, string basePath, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            var created = await multiLingual.Languages.CreateAsync(new NewLanguage
            {
                Code      = request.Code ?? string.Empty,
                Name      = request.Name ?? string.Empty,
                Direction = TextDirections.Parse(request.Direction),
                IsActive  = request.IsActive,
                IsDefault = request.IsDefault
            }, ct);

            return TypedResults.Created($"{basePath}/{created.Code}", LanguageResponse.From(created));
        }

        private static async Task<Ok<LanguageResponse>> UpdateAsync(
            string code, UpdateLanguageRequest request, IMultiLingualManager multiLingual, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            var updated = await multiLingual.Languages.UpdateAsync(code, new LanguageUpdate
            {
                Name      = request.Name ?? string.Empty,
                Direction = TextDirections.Parse(request.Direction),
                IsActive  = request.IsActive
            }, ct);

            return TypedResults.Ok(LanguageResponse.From(updated));
        }

        private static async Task<NoContent> DeleteAsync(string code, IMultiLingualManager multiLingual, CancellationToken ct)
        {
            await multiLingual.Languages.DeleteAsync(code, ct);
            return TypedResults.NoContent();
        }

        private static async Task<Ok<LanguageResponse>> SetDefaultAsync(string code, IMultiLingualManager multiLingual, CancellationToken ct) =>
            TypedResults.Ok(LanguageResponse.From(await multiLingual.Languages.SetDefaultAsync(code, ct)));

        private static async Task<Ok<PurgeTranslationsResponse>> PurgeAsync(string code, IMultiLingualManager multiLingual, CancellationToken ct)
        {
            var language = await multiLingual.Languages.GetAsync(code, ct);
            var deleted  = await multiLingual.Languages.PurgeTranslationsAsync(language.Code, ct);
            return TypedResults.Ok(new PurgeTranslationsResponse(language.Code, deleted));
        }
    }
}
