using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SampleApp.Infrastructure.OpenApi;

/// <summary>
/// Gives the generated document a title and a short orientation blurb, so the Scalar sidebar
/// explains what the API is before anyone clicks an endpoint.
/// </summary>
public sealed class ApiDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        document.Info = new OpenApiInfo
        {
            Title = "Multilingual API",
            Version = "v1",
            Description =
                """
                Translatable content for any entity, driven by a single `[MultiLingual]` attribute.

                **Try it:** every content endpoint takes an `Accept-Language` header. Fetch
                `GET /api/products` with `Accept-Language: en`, then `ar`, then `fr` and watch `name` and
                `description` change while `sku` and `price` stay put — those two are ordinary
                columns, the other two live in the generic `Translations` table.

                `MOUSE-01` has no French translation on purpose: request it with `fr` and you
                get the English text back, which is the default-language fallback doing its job.
                The `Content-Language` response header always tells you which language you were
                actually served.

                `Product` declares `[MultiLingual]` on individual properties; `Category` declares it
                once on the class. Same behaviour, and neither controller contains a line of
                translation code.
                """
        };

        return Task.CompletedTask;
    }
}
