using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace CaseLedger.Api.EvidenceStorage;

public sealed class EvidenceUploadOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (!string.Equals(
                operation.OperationId,
                "AddCaseEvidence",
                StringComparison.Ordinal))
        {
            return;
        }

        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Description = "Upload bytes with multipart/form-data. JSON metadata registration is compatibility-only.",
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.OrdinalIgnoreCase)
            {
                ["multipart/form-data"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string>(["file"], StringComparer.Ordinal),
                        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                        {
                            ["file"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Format = "binary"
                            }
                        }
                    }
                },
                ["application/json"] = new()
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string>(
                            ["fileName", "sizeBytes", "mediaType", "sha256"],
                            StringComparer.Ordinal),
                        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                        {
                            ["fileName"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                MinLength = 1,
                                MaxLength = 255
                            },
                            ["sizeBytes"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.Integer,
                                Format = "int64",
                                Minimum = "0"
                            },
                            ["mediaType"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                MinLength = 3,
                                MaxLength = 150
                            },
                            ["sha256"] = new OpenApiSchema
                            {
                                Type = JsonSchemaType.String,
                                Pattern = "^[0-9a-fA-F]{64}$"
                            }
                        }
                    }
                }
            }
        };
    }
}
