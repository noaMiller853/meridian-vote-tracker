using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Models;

namespace Meridian.Api.Http;

/// <summary>
/// Describes the X-User header (the user switcher) as an API-key security scheme in the OpenAPI document, so the
/// generated client and Swagger UI ("Authorize") send it with every /api call.
/// </summary>
public sealed class UserHeaderSecurityScheme : IOpenApiDocumentTransformer
{
    private const string SchemeId = "X-User";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes[SchemeId] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = CurrentUserMiddleware.Header,
            Description = "Acting user: Dana or Omar (Analyst), Lena (Admin). Simplified identification, spec section 3.",
        };
        document.SecurityRequirements.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = SchemeId } }] = [],
        });
        return Task.CompletedTask;
    }
}
