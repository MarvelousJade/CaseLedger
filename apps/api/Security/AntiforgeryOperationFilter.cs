using Microsoft.AspNetCore.Antiforgery;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace CaseLedger.Api.Security;

public sealed class AntiforgeryOperationFilter : IOperationFilter
{
    private const string HeaderName = "X-CSRF-TOKEN";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Parameters ??= [];
        if (!IsUnsafeMethod(context.ApiDescription.HttpMethod) ||
            !RequiresAntiforgery(context) ||
            operation.Parameters.Any(parameter =>
                string.Equals(parameter.Name, HeaderName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = HeaderName,
            In = ParameterLocation.Header,
            Required = true,
            Description =
                "Required together with the matching antiforgery cookie. Obtain both from GET /api/auth/antiforgery; the bundled Swagger UI does this automatically.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String
            }
        });
    }

    private static bool RequiresAntiforgery(OperationFilterContext context) =>
        context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<IAntiforgeryMetadata>()
            .Any(metadata => metadata.RequiresValidation);

    private static bool IsUnsafeMethod(string? method) =>
        method is not null &&
        method.ToUpperInvariant() is "POST" or "PUT" or "PATCH" or "DELETE";
}
