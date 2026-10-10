using DotNetBoost.MultiLingual.API;
using DotNetBoost.MultiLingual.Core;
using DotNetBoost.MultiLingual.Core.Interfaces;
using DotNetBoost.MultiLingual.Core.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace DotNetBoost.MultiLingual.API
{
    /// <summary>
    /// The entity names <c>MapTranslationEndpoints</c> serves. Registered as a singleton by
    /// <c>ExposeOverHttp(...)</c>; an unlisted name is not routed at all.
    /// </summary>
    public sealed class ExposedTranslationEntities
    {
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every exposed name, in the order it would be documented (sorted).</summary>
        public IReadOnlyCollection<string> Names => _names.Order(StringComparer.OrdinalIgnoreCase).ToArray();

        /// <summary>True when translations for <paramref name="entityName"/> may be read and written over HTTP.</summary>
        public bool IsExposed(string? entityName) => entityName is not null && _names.Contains(entityName);

        internal void Add(string entityName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(entityName);

            var trimmed = entityName.Trim();
            if (trimmed.Length > MultiLingualMetadata.MaxEntityNameLength)
            {
                throw new ArgumentException(
                    $"Entity name '{trimmed[..32]}…' is {trimmed.Length} characters long; translation " +
                    $"entity names hold at most {MultiLingualMetadata.MaxEntityNameLength}.", nameof(entityName));
            }

            _names.Add(trimmed);
        }
    }

    /// <summary>
    /// Names the entities whose translations may be read and written over HTTP, from
    /// <c>ExposeOverHttp(...)</c>. Nothing is exposed until it is listed here.
    /// </summary>
    public sealed class TranslationExposureBuilder
    {
        private readonly ExposedTranslationEntities _exposed;

        internal TranslationExposureBuilder(ExposedTranslationEntities exposed) => _exposed = exposed;

        /// <summary>
        /// Exposes an entity by name — for a subject whose CLR type the application does not own,
        /// such as another library's <c>MediaFile</c>.
        /// </summary>
        /// <exception cref="ArgumentException">The name is blank or too long.</exception>
        public TranslationExposureBuilder Allow(string entityName)
        {
            _exposed.Add(entityName);
            return this;
        }

        /// <summary>
        /// Exposes an entity by type. The name is the one translations are stored under, resolved
        /// through <c>[MultiLingualEntity]</c> exactly as the rest of the library resolves it, so
        /// renaming the class cannot silently change the route.
        /// </summary>
        public TranslationExposureBuilder Allow<TEntity>() where TEntity : class =>
            Allow(MultiLingualMetadata.EntityNameFor(typeof(TEntity)));
    }

    /// <summary>Every stored language of one subject.</summary>
    /// <param name="EntityName">The entity name the rows are filed under.</param>
    /// <param name="EntityId">The subject's id, as stored.</param>
    /// <param name="Translations">languageCode → (propertyName → value).</param>
    public sealed record TranslationsResponse(
        string EntityName,
        string EntityId,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Translations);

    /// <summary>One subject's values in the request's language, with the fallback applied.</summary>
    /// <param name="EntityName">The entity name the rows are filed under.</param>
    /// <param name="EntityId">The subject's id, as stored.</param>
    /// <param name="LanguageCode">The language the values were resolved for.</param>
    /// <param name="Values">propertyName → value.</param>
    public sealed record ResolvedTranslationsResponse(
        string EntityName,
        string EntityId,
        string LanguageCode,
        IReadOnlyDictionary<string, string?> Values);

    /// <summary>Many subjects' values in the request's language, with the fallback applied.</summary>
    /// <param name="EntityName">The entity name the rows are filed under.</param>
    /// <param name="LanguageCode">The language the values were resolved for.</param>
    /// <param name="Subjects">entityId → (propertyName → value). Ids with nothing stored are absent.</param>
    public sealed record ResolvedTranslationsBatchResponse(
        string EntityName,
        string LanguageCode,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> Subjects);

    /// <summary>Body of <c>PUT {prefix}/{entityName}/{entityId}</c>.</summary>
    public sealed record WriteTranslationsRequest
    {
        /// <summary>
        /// languageCode → (propertyName → value), written as one batch. A <c>null</c> value
        /// removes the stored row; a language or property that is absent is left alone.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>>? Translations { get; init; }
    }

    /// <summary>Body of <c>POST {prefix}/{entityName}/resolve</c>.</summary>
    public sealed record ResolveTranslationsRequest
    {
        /// <summary>The ids to resolve, read in one query.</summary>
        public IReadOnlyCollection<string>? EntityIds { get; init; }
    }

    /// <summary>Result of deleting a subject's translations.</summary>
    /// <param name="EntityName">The entity name.</param>
    /// <param name="EntityId">The subject's id.</param>
    /// <param name="DeletedTranslations">How many rows were removed.</param>
    public sealed record DeleteTranslationsResponse(string EntityName, string EntityId, int DeletedTranslations);
}

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Choosing which entities' translations are reachable over HTTP.</summary>
    public static class TranslationExposureBuilderExtensions
    {
        /// <summary>
        /// Names the entities whose translations <c>MapTranslationEndpoints()</c> serves:
        /// <code>
        /// builder.Services
        ///     .AddMultiLingual()
        ///     .UseEntityFrameworkCore&lt;AppDbContext&gt;()
        ///     .ExposeOverHttp(e =&gt; e
        ///         .Allow("MediaFile")        // by name, for a type the application does not own
        ///         .Allow&lt;Product&gt;()          // by type, name resolved through [MultiLingualEntity]
        ///         .Allow&lt;Category&gt;())
        ///     .Build();
        /// </code>
        /// <para>
        /// <b>This opt-in is not optional.</b> The routes take the entity name from the URL, so
        /// without a list they would be a write-anything-anywhere surface: any caller could write
        /// translation rows for any entity in the system, including ones the application never
        /// meant to expose. An unlisted name is not routed, and answers 404 — not 403, which would
        /// confirm which names exist.
        /// </para>
        /// <para>
        /// Calling it more than once adds to the same list. Authorization stays the
        /// application's: compose it on the route group, as for the language endpoints.
        /// </para>
        /// </summary>
        public static MultiLingualBuilder ExposeOverHttp(
            this MultiLingualBuilder builder,
            Action<TranslationExposureBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            // Registered as an instance, not as a type: several calls have to add to ONE list, and
            // MapTranslationEndpoints() reads that list from the service collection's own
            // descriptor — before the application is built, which is when it decides what to map.
            var exposed = (ExposedTranslationEntities?)builder.Services
                .FirstOrDefault(s => s.ServiceType == typeof(ExposedTranslationEntities))?.ImplementationInstance;

            if (exposed is null)
            {
                exposed = new ExposedTranslationEntities();
                builder.Services.AddSingleton(exposed);
            }

            configure(new TranslationExposureBuilder(exposed));
            return builder;
        }
    }

    /// <summary>Maps the translation read/write endpoints.</summary>
    public static partial class TranslationEndpointRouteBuilderExtensions
    {
        /// <summary>
        /// Maps translation reads and writes under <paramref name="prefix"/>, for the entity names
        /// listed with <c>ExposeOverHttp(...)</c>:
        /// <code>
        /// GET    {prefix}/{entityName}/{entityId}            every stored language
        /// GET    {prefix}/{entityName}/{entityId}/resolved   the request's language, fallback applied
        /// PUT    {prefix}/{entityName}/{entityId}            atomic batch upsert, all languages
        /// POST   {prefix}/{entityName}/resolve               many ids at once, the request's language
        /// DELETE {prefix}/{entityName}/{entityId}            every language
        /// </code>
        /// <para>
        /// With nothing exposed, <b>nothing is mapped</b> and a warning says so at startup, rather
        /// than serving a surface on which every request 404s.
        /// </para>
        /// <para>
        /// <b>They are anonymous</b>, as every <c>Map…</c> in ASP.NET Core is, and they WRITE
        /// content. The route group is returned so protecting them takes one line:
        /// <code>
        /// app.MapTranslationEndpoints().RequireAuthorization("content-editor");
        /// </code>
        /// </para>
        /// </summary>
        public static RouteGroupBuilder MapTranslationEndpoints(
            this IEndpointRouteBuilder endpoints,
            string prefix = "/api/translations")
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

            var group = endpoints.MapGroup(prefix.TrimEnd('/'))
                .WithTags("Translations")
                .AddEndpointFilter<MultiLingualProblemFilter>();

            var exposed = endpoints.ServiceProvider.GetService<ExposedTranslationEntities>();
            if (exposed is null || exposed.Names.Count == 0)
            {
                if (endpoints.ServiceProvider.GetService<ILoggerFactory>() is { } loggerFactory)
                    Log.NothingExposed(loggerFactory.CreateLogger(typeof(TranslationEndpointRouteBuilderExtensions)));

                return group;
            }

            group.MapGet("/{entityName}/{entityId}", GetAllAsync)
                .WithSummary("Gets every stored language of one subject.")
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapGet("/{entityName}/{entityId}/resolved", ResolveAsync)
                .WithSummary("Gets one subject's values in the request's language, with the default-language fallback.")
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapPut("/{entityName}/{entityId}", WriteAsync)
                .WithSummary("Writes every supplied language of one subject, as one batch.")
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapPost("/{entityName}/resolve", ResolveManyAsync)
                .WithSummary("Resolves many ids in the request's language, in one query.")
                .ProducesProblem(StatusCodes.Status400BadRequest)
                .ProducesProblem(StatusCodes.Status404NotFound);

            group.MapDelete("/{entityName}/{entityId}", DeleteAsync)
                .WithSummary("Deletes every translation of one subject.")
                .ProducesProblem(StatusCodes.Status404NotFound);

            return group;
        }

        private static async Task<Results<Ok<TranslationsResponse>, NotFound, ProblemHttpResult>> GetAllAsync(
            string entityName,
            string entityId,
            ExposedTranslationEntities exposed,
            IMultiLingualManager multiLingual,
            CancellationToken ct)
        {
            if (!exposed.IsExposed(entityName))
                return TypedResults.NotFound();

            if (BlankId(entityId) is { } invalid)
                return invalid;

            var subject = multiLingual.For(entityName, entityId);
            var stored  = await subject.GetAllAsync(ct);

            return TypedResults.Ok(new TranslationsResponse(subject.EntityName, subject.EntityId, stored));
        }

        private static async Task<Results<Ok<ResolvedTranslationsResponse>, NotFound, ProblemHttpResult>> ResolveAsync(
            string entityName,
            string entityId,
            ExposedTranslationEntities exposed,
            IMultiLingualManager multiLingual,
            CancellationToken ct)
        {
            if (!exposed.IsExposed(entityName))
                return TypedResults.NotFound();

            if (BlankId(entityId) is { } invalid)
                return invalid;

            var subject = multiLingual.For(entityName, entityId);
            var values  = await subject.GetAsync(ct);

            return TypedResults.Ok(new ResolvedTranslationsResponse(
                subject.EntityName, subject.EntityId, multiLingual.CurrentLanguage.Code, values));
        }

        private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> WriteAsync(
            string entityName,
            string entityId,
            WriteTranslationsRequest request,
            ExposedTranslationEntities exposed,
            IMultiLingualManager multiLingual,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!exposed.IsExposed(entityName))
                return TypedResults.NotFound();

            if (BlankId(entityId) is { } invalid)
                return invalid;

            var subject = multiLingual.For(entityName, entityId);

            foreach (var (languageCode, values) in request.Translations ?? EmptyTranslations)
            {
                // An unknown language throws here, where the caller named it, rather than after
                // part of the batch has been staged.
                subject.Set(languageCode, values ?? NoValues);
            }

            // One store write for every language in the batch.
            await subject.SaveAsync(ct);
            return TypedResults.NoContent();
        }

        private static async Task<Results<Ok<ResolvedTranslationsBatchResponse>, NotFound>> ResolveManyAsync(
            string entityName,
            ResolveTranslationsRequest request,
            ExposedTranslationEntities exposed,
            IMultiLingualManager multiLingual,
            CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!exposed.IsExposed(entityName))
                return TypedResults.NotFound();

            var ids      = (request.EntityIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Cast<object>().ToArray();
            var subjects = multiLingual.ForMany(entityName, ids);

            return TypedResults.Ok(new ResolvedTranslationsBatchResponse(
                subjects.EntityName, multiLingual.CurrentLanguage.Code, await subjects.GetAsync(ct)));
        }

        private static async Task<Results<Ok<DeleteTranslationsResponse>, NotFound, ProblemHttpResult>> DeleteAsync(
            string entityName,
            string entityId,
            ExposedTranslationEntities exposed,
            IMultiLingualManager multiLingual,
            CancellationToken ct)
        {
            if (!exposed.IsExposed(entityName))
                return TypedResults.NotFound();

            if (BlankId(entityId) is { } invalid)
                return invalid;

            var subject = multiLingual.For(entityName, entityId);
            var deleted = await subject.DeleteAsync(ct);

            return TypedResults.Ok(new DeleteTranslationsResponse(subject.EntityName, subject.EntityId, deleted));
        }

        /// <summary>
        /// A problem when the route's id is blank — <c>/MediaFile/%20</c> matches the route but
        /// addresses no subject, and must not file a row under a whitespace key. <c>null</c> when
        /// the id is usable.
        /// </summary>
        private static ProblemHttpResult? BlankId(string entityId) =>
            string.IsNullOrWhiteSpace(entityId)
                ? TypedResults.Problem(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title  = "Invalid entity id",
                    Detail = "The entity id is blank, so it addresses no subject."
                })
                : null;

        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> EmptyTranslations =
            new Dictionary<string, IReadOnlyDictionary<string, string?>>();

        private static readonly IReadOnlyDictionary<string, string?> NoValues = new Dictionary<string, string?>();

        private static partial class Log
        {
            [LoggerMessage(EventId = 4000, Level = LogLevel.Warning,
                Message = "MapTranslationEndpoints() mapped nothing: no entity names are exposed. Add " +
                          "ExposeOverHttp(e => e.Allow(\"…\")) to the AddMultiLingual() chain to serve them.")]
            public static partial void NothingExposed(ILogger logger);
        }
    }
}
