using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Streaik.OpenAI.Models.Decisions;
using Streaik.Providers;
using Streaik.Vercel.Models;

namespace Streaik.Gateway;

public static partial class StreaikGateway
{
    private static void MapDecisions(IEndpointRouteBuilder endpoints)
    {
        MapGatewayPost<OpenAIDecisionRequest>(endpoints, "/v1/decisions", OpenAIDecisionsAsync)
            .Produces<OpenAIDecisionResponse>(200, JsonMediaType);
        MapGatewayPost<DecisionRequest>(endpoints, "/api/decisions", SdkDecisionsAsync, Protocol.Vercel)
            .Produces<DecisionResponse>(200, JsonMediaType);
    }

    private static async Task OpenAIDecisionsAsync(HttpContext context)
    {
        var request = await ReadAsync<OpenAIDecisionRequest>(context, JsonSerializerOptions.Web);
        var call = new GatewayCall(context, request.Model);
        request.Model = call.NativeModel;
        var result = await call.Require<IDecisionsProvider>().DecideAsync(request, call.Headers, call.Ct);
        await context.Response.WriteAsJsonAsync(result, JsonSerializerOptions.Web, call.Ct);
    }

    private static async Task SdkDecisionsAsync(HttpContext context)
    {
        var request = await ReadAsync<DecisionRequest>(context, JsonSerializerOptions.Web);
        var call = new GatewayCall(context, request.Model);
        request.Model = call.NativeModel;

        // SDK header options obey the same provider allowlist as actual HTTP headers.
        var headers = new Dictionary<string, string>(call.Headers, StringComparer.OrdinalIgnoreCase);
        if (request.Headers is { Count: > 0 })
        {
            var supplied = new HeaderDictionary();
            foreach (var (name, value) in request.Headers)
                if (value is not null && !value.Any(char.IsControl)) supplied[name] = value;
            foreach (var (name, value) in GatewayHeaders.Select(supplied, call.Require<ProviderRegistration>()))
                headers.TryAdd(name, value);
        }

        var result = await call.Require<IDecisionsProvider>().DecideAsync(request, headers, call.Ct);
        await context.Response.WriteAsJsonAsync(result, JsonSerializerOptions.Web, call.Ct);
    }
}
