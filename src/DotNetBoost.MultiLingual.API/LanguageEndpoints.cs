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
        /// Returns the route group, so the endpoints can be secured in one call —
        /// <c>app.MapLanguageEndpoints().RequireAuthorization("admin")</c>. They change what every
        /// client sees; do not leave them anonymous in production.
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

            group.MapPost("/", (CreateLanguageRequest request, ILanguageManager languages, CancellationToken ct)
                    => CreateAsync(request, languages, basePath, ct))
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

        private static async Task<Ok<List<LanguageResponse>>> GetAllAsync(ILanguageManager languages, CancellationToken ct) =>
            TypedResults.Ok((await languages.GetAllAsync(ct)).Select(LanguageResponse.From).ToList());

        private static async Task<Ok<LanguageResponse>> GetAsync(string code, ILanguageManager languages, CancellationToken ct) =>
            TypedResults.Ok(LanguageResponse.From(await languages.GetAsync(code, ct)));

        private static async Task<Created<LanguageResponse>> CreateAsync(
            CreateLanguageRequest request, ILanguageManager languages, string basePath, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            var created = await languages.CreateAsync(new NewLanguage
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
            string code, UpdateLanguageRequest request, ILanguageManager languages, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            var updated = await languages.UpdateAsync(code, new LanguageUpdate
            {
                Name      = request.Name ?? string.Empty,
                Direction = TextDirections.Parse(request.Direction),
                IsActive  = request.IsActive
            }, ct);

            return TypedResults.Ok(LanguageResponse.From(updated));
        }

        private static async Task<NoContent> DeleteAsync(string code, ILanguageManager languages, CancellationToken ct)
        {
            await languages.DeleteAsync(code, ct);
            return TypedResults.NoContent();
        }

        private static async Task<Ok<LanguageResponse>> SetDefaultAsync(string code, ILanguageManager languages, CancellationToken ct) =>
            TypedResults.Ok(LanguageResponse.From(await languages.SetDefaultAsync(code, ct)));

        private static async Task<Ok<PurgeTranslationsResponse>> PurgeAsync(string code, ILanguageManager languages, CancellationToken ct)
        {
            var language = await languages.GetAsync(code, ct);
            var deleted  = await languages.PurgeTranslationsAsync(language.Code, ct);
            return TypedResults.Ok(new PurgeTranslationsResponse(language.Code, deleted));
        }
    }
}
