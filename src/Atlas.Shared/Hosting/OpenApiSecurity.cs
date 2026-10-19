using Atlas.Shared.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Atlas.Shared.Hosting;

/// <summary>
/// Describes how each endpoint authenticates, so Swagger UI offers an Authorize button: a bearer token for officers,
/// branch staff and services, the application token for applicants.
/// </summary>
internal sealed class OpenApiSecurity : IOpenApiDocumentTransformer, IOpenApiOperationTransformer
{
    private const string Bearer = "Bearer";
    private const string ApplicationToken = "ApplicationToken";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.AddComponent<IOpenApiSecurityScheme>(Bearer, new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "A development token from the simulator host: POST http://localhost:5300/dev/token.",
        });
        document.AddComponent<IOpenApiSecurityScheme>(ApplicationToken, new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApplicantAuthentication.TokenHeader,
            Description = "The applicationToken returned when the application was created.",
        });
        return Task.CompletedTask;
    }

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var policies = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizeData>().Select(a => a.Policy).ToList();
        if (policies.Count == 0)
        {
            return Task.CompletedTask;
        }

        var scheme = policies.Contains(Policies.Applicant) ? ApplicationToken : Bearer;
        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(scheme, context.Document)] = [] });
        return Task.CompletedTask;
    }
}
