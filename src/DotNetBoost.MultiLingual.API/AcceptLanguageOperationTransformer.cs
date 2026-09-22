#if NET10_0_OR_GREATER
using DotNetBoost.MultiLingual.API;
using DotNetBoost.MultiLingual.Core.Interfaces;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;
using System.Text.Json.Nodes;

namespace DotNetBoost.MultiLingual.API
{
    /// <summary>
    /// Adds the <c>Accept-Language</c> header parameter to every operation not marked
    /// <see cref="LanguageAgnosticAttribute"/>.
    /// <para>
    /// The language is resolved by <see cref="LanguageResolutionMiddleware"/> rather than bound
    /// to a handler parameter, so nothing advertises it — which would leave a UI such as Scalar
    /// or Swagger with no way to exercise it. This puts it back in the document, with the live
    /// list of active codes as the enum, so the UI offers a dropdown of exactly the languages
    /// this instance serves.
    /// </para>
    /// </summary>
    /// <param name="catalog">The language list.</param>
    internal sealed class AcceptLanguageOperationTransformer(ILanguageCatalog catalog) : IOpenApiOperationTransformer
    {
        /// <inheritdoc/>
        public async Task TransformAsync(
            OpenApiOperation operation,
            OpenApiOperationTransformerContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ArgumentNullException.ThrowIfNull(context);

            var metadata = context.Description.ActionDescriptor.EndpointMetadata;
            if (metadata.OfType<LanguageAgnosticAttribute>().Any())
                return;

            var languages   = await catalog.GetAllAsync(cancellationToken);
            var activeCodes = languages.Where(l => l.IsActive).Select(l => l.Code).ToList();
            if (activeCodes.Count == 0)
                return;

            var defaultCode = languages.FirstOrDefault(l => l.IsDefault)?.Code ?? activeCodes[0];

            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name        = HeaderNames.AcceptLanguage,
                In          = ParameterLocation.Header,
                Required    = false,
                Description =
                    "Language to read and write content in. Standard quality-weighted negotiation is " +
                    "supported, e.g. 'fr-FR, fr;q=0.9, en;q=0.5'. " +
                    $"Unknown or inactive codes fall back to the default language ('{defaultCode}'); " +
                    "the Content-Language response header always reports what you actually got.",
                Schema = new OpenApiSchema
                {
                    Type    = JsonSchemaType.String,
                    Enum    = [.. activeCodes.Select(code => (JsonNode)code)],
                    Example = defaultCode
                }
            });
        }
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>OpenAPI registration.</summary>
    public static class MultiLingualOpenApiOptionsExtensions
    {
        /// <summary>
        /// Documents <c>Accept-Language</c> on every operation that is not language-agnostic:
        /// <c>services.AddOpenApi(o =&gt; o.AddAcceptLanguageParameter())</c>.
        /// </summary>
        public static OpenApiOptions AddAcceptLanguageParameter(this OpenApiOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return options.AddOperationTransformer<AcceptLanguageOperationTransformer>();
        }
    }
}
#endif
