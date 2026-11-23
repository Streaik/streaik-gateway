using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.OpenApi;
using Streaik.ChatCompletions.Models;
using Streaik.Messages;
using Streaik.OpenAI.Model.Providers.OpenAI.Live;
using Streaik.OpenAI.Models;
using Streaik.OpenAI.Models.Decisions;
using Streaik.Responses;
using Streaik.Vercel.Models;

namespace Streaik.Gateway.Sample;

internal static class GatewayOpenApi
{
    public static void Configure(OpenApiOptions options)
    {
        options.AddOperationTransformer(DescribeOperationAsync);
        options.AddSchemaTransformer(DescribeWireSchemaAsync);
    }

    private static async Task DescribeOperationAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken ct)
    {
        // These handlers read RouteValues directly, so API Explorer has no bound parameters to report.
        if (context.Description.RelativePath is { } path && path.Contains('{'))
        {
            foreach (var parameter in RoutePatternFactory.Parse(path).Parameters)
            {
                operation.Parameters ??= [];
                if (operation.Parameters.Any(p => p.In == ParameterLocation.Path && p.Name == parameter.Name)) continue;
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = parameter.Name,
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = Text(),
                    Description = parameter.Name == "model" ? "A provider-qualified model ID (provider/model)." : null
                });
            }
        }

        var relativePath = context.Description.RelativePath?.TrimEnd('/');
        if (context.Description.HttpMethod == "GET" && relativePath is "v1/skills" or "v1/skills/{providerId}/{skillId}/versions")
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter { Name = "after", In = ParameterLocation.Query, Schema = Text() });
            operation.Parameters.Add(new OpenApiParameter { Name = "limit", In = ParameterLocation.Query, Schema = new OpenApiSchema { Type = JsonSchemaType.Integer } });
            operation.Parameters.Add(new OpenApiParameter { Name = "order", In = ParameterLocation.Query, Schema = Text() });
        }

        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<GatewayRequestBodyMetadata>().LastOrDefault() is { } body)
        {
            var requestBody = new OpenApiRequestBody { Required = true, Content = new Dictionary<string, IOpenApiMediaType>() };
            if (body.JsonType is { } jsonType)
            {
                requestBody.Content["application/json"] = new OpenApiMediaType
                {
                    Schema = jsonType == typeof(JsonObject) ? RealtimeClientSecret() : await context.GetOrCreateSchemaAsync(jsonType, cancellationToken: ct),
                    Example = RequestExample(jsonType)
                };
            }
            if (body.Multipart is { } multipart)
                requestBody.Content["multipart/form-data"] = new OpenApiMediaType { Schema = MultipartSchema(multipart) };
            operation.RequestBody = requestBody;
        }

        // ApiExplorer associates one CLR response type with each status, even when the wire formats differ.
        if (operation.Responses is null) return;
        foreach (var response in operation.Responses.Values)
        {
            if (response.Content is not { } content) continue;
            foreach (var mediaType in content.Keys.ToArray())
            {
                if (mediaType is "text/event-stream" or "text/plain" or "text/vtt")
                    content[mediaType] = new OpenApiMediaType { Schema = Text() };
                else if (mediaType.StartsWith("audio/", StringComparison.Ordinal) || mediaType is "application/octet-stream" or "application/zip")
                    content[mediaType] = new OpenApiMediaType { Schema = Binary() };
            }
        }
    }

    private static Task DescribeWireSchemaAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken _)
    {
        var type = context.JsonTypeInfo.Type;
        if (type == typeof(ResponseInput) || type == typeof(ChatMessageContent) || type == typeof(MessagesContent)
            || type == typeof(OpenAIDecisionInput) || type == typeof(OpenAIDecisionQuestionInstructions) || type == typeof(OpenAIDecisionMessageContent))
        {
            // These custom converters accept strings or arrays of protocol-specific objects.
            schema.AnyOf = [Text(), ArrayOf(new OpenApiSchema { Type = JsonSchemaType.Object })];
            if (schema.Type is { } existing && existing.HasFlag(JsonSchemaType.Null))
                schema.AnyOf.Add(new OpenApiSchema { Type = JsonSchemaType.Null });
            schema.Type = null;
        }
        else if (type == typeof(OpenAIDecisionChoiceValue))
        {
            schema.AnyOf = [Text(), new OpenApiSchema { Type = JsonSchemaType.Boolean }];
            schema.Type = null;
        }

        // Some gateway-required fields are nullable on the shared upstream DTOs.
        string[] required = type switch
        {
            _ when type == typeof(ChatCompletionOptions) => ["model", "messages"],
            _ when type == typeof(ResponseRequest) => ["model"],
            _ when type == typeof(OpenAIDecisionRequest) => ["model", "input", "questions"],
            _ when type == typeof(DecisionRequest) => ["model", "state", "questions"],
            _ when type == typeof(OpenAIImageEditRequest) => ["model", "prompt", "images"],
            _ when type == typeof(OpenAIImageGenerationRequest) || type == typeof(ImageRequest) || type == typeof(VideoRequest) => ["model", "prompt"],
            _ when type == typeof(OpenAIEmbeddingRequest) || type == typeof(AudioSpeechRequest) => ["model", "input"],
            _ when type == typeof(EmbeddingRequest) => ["model", "values"],
            _ when type == typeof(RerankingRequest) => ["model", "query", "documents"],
            _ when type == typeof(RealtimeSessionRequest) => ["sdp"],
            _ when type == typeof(LiveSessionRequest) => ["session", "transport"],
            _ => []
        };
        foreach (var field in required)
            if (schema.Properties?.ContainsKey(field) == true) (schema.Required ??= new HashSet<string>()).Add(field);
        return Task.CompletedTask;
    }

    private static JsonNode? RequestExample(Type type)
    {
        string? json = type == typeof(ChatCompletionOptions) ? """{"model":"openai/gpt-4.1-mini","messages":[{"role":"user","content":"Hello"}]}"""
            : type == typeof(ResponseRequest) ? """{"model":"openai/gpt-4.1-mini","input":"Hello"}"""
            : type == typeof(OpenAIDecisionRequest) ? """{"model":"openai/gpt-6-luna","input":"The shipment arrived damaged.","questions":[{"type":"predicate","name":"damaged","instructions":"Was there damage?"}]}"""
            : type == typeof(DecisionRequest) ? """{"model":"fireworks/accounts/fireworks/models/rune","state":"The shipment arrived damaged.","questions":{"damaged":{"type":"boolean","instructions":"Was there damage?"}}}"""
            : type == typeof(MessagesRequest) ? """{"model":"anthropic/your-model","max_tokens":128,"messages":[{"role":"user","content":"Hello"}]}"""
            : type == typeof(JsonObject) ? """{"session":{"model":"openai/gpt-realtime"}}"""
            : null;
        return json is null ? null : JsonNode.Parse(json);
    }

    private static OpenApiSchema RealtimeClientSecret() => new()
    {
        Type = JsonSchemaType.Object,
        Description = "Provide the provider-qualified model in session.model or as the top-level model.",
        Properties = new Dictionary<string, IOpenApiSchema>
        {
            ["model"] = Text(),
            ["session"] = new OpenApiSchema
            {
                Type = JsonSchemaType.Object,
                Properties = new Dictionary<string, IOpenApiSchema> { ["model"] = Text(), ["type"] = Text() }
            }
        }
    };

    private static OpenApiSchema MultipartSchema(GatewayMultipartBody multipart) => multipart switch
    {
        GatewayMultipartBody.SkillUpload => new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Required = new HashSet<string> { "files" },
            Properties = new Dictionary<string, IOpenApiSchema> { ["files"] = Binary() }
        },
        GatewayMultipartBody.ImageEdit => new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Required = new HashSet<string> { "model", "prompt", "image" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["model"] = Text(), ["prompt"] = Text(), ["image"] = ArrayOf(Binary(), 1), ["mask"] = Binary(),
                ["size"] = Text(), ["quality"] = Text(), ["output_format"] = Text(), ["n"] = new OpenApiSchema { Type = JsonSchemaType.Integer },
                ["stream"] = new OpenApiSchema { Type = JsonSchemaType.Boolean }
            }
        },
        GatewayMultipartBody.AudioTranscription => new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Required = new HashSet<string> { "model", "file" },
            Properties = new Dictionary<string, IOpenApiSchema>
            {
                ["model"] = Text(), ["file"] = Binary(), ["language"] = Text(), ["prompt"] = Text(), ["response_format"] = Text(),
                ["stream"] = new OpenApiSchema { Type = JsonSchemaType.Boolean }
            }
        },
        _ => throw new ArgumentOutOfRangeException(nameof(multipart))
    };

    private static OpenApiSchema Text() => new() { Type = JsonSchemaType.String };
    private static OpenApiSchema Binary() => new() { Type = JsonSchemaType.String, Format = "binary" };
    private static OpenApiSchema ArrayOf(IOpenApiSchema items, int? minItems = null) => new() { Type = JsonSchemaType.Array, Items = items, MinItems = minItems };
}
