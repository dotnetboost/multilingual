using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace SampleApp.Dapper.Infrastructure.OpenApi;

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
            Title = "Multilingual API (Dapper)",
            Version = "v1",
            Description =
                """
                The same API as the EF Core sample, over the Dapper provider. Same routes, same
                request and response bodies — literally the same DTO files — so the dashboard can be
                pointed at any of the three.

                **Try it:** every content endpoint takes an `Accept-Language` header. Fetch
                `GET /api/products` with `Accept-Language: en`, then `ar`, then `fr` and watch `name`
                and `description` change while `sku` and `price` stay put — those two are ordinary
                columns on `Products`, the other two live in `Translations`.

                `MOUSE-01` has no French translation on purpose: request it with `fr` and you get the
                English text back, which is the default-language fallback doing its job.

                What differs from EF Core is inside the controllers, not at the edge: there is no
                change tracker, so they localize after a query, save translations once the row
                exists, and apply the base-language rules themselves. `search` and `sort` on
                `GET /api/products` run in memory for the same reason.

                Unlike the MongoDB sample, every request here runs in a real transaction, so a row
                and its translations commit or roll back together — see docs/dapper.md. The two
                library tables were created at startup by `migrateSchema: true`.
                """
        };

        return Task.CompletedTask;
    }
}
