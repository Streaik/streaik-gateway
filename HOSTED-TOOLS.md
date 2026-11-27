# Hosted tools

Both routes use their existing `tools` array. Supported hosted names are `mcp`, `skills`, `tool_search`, and `resource_search`.

**Direct API Chat** uses the Vercel tool shape. Put hosted options inside `inputSchema`, under a key matching the tool's `name`. The gateway consumes that entry; it is not a model-facing function argument. A browser function named `mcp` with an ordinary schema and no hosted options remains a browser function.

```json
{
  "tools": [{
    "name": "mcp",
    "inputSchema": {
      "type": "object",
      "mcp": {
        "server_label": "inventory",
        "server_url": "https://inventory.example.com/mcp",
        "headers": { "Authorization": "Bearer <MCP access token>" },
        "allowed_tools": ["lookup"],
        "defer_loading": false,
        "require_approval": "never"
      }
    }
  }, {
    "name": "resource_search",
    "inputSchema": { "type": "object", "resource_search": {} }
  }]
}
```

**Responses** uses a matching `name` and `type` to identify a Streaik descriptor. Ordinary provider-native entries such as `{ "type": "mcp", ... }` without the name still follow their existing provider route. For gateway-hosted tools, the gateway removes hosted configuration before inference and sends only the exposed function declarations and relevant instructions.

```json
{
  "tools": [{
    "type": "mcp",
    "name": "mcp",
    "server_label": "inventory",
    "server_url": "https://inventory.example.com/mcp",
    "execution": "server",
    "headers": { "Authorization": "Bearer <MCP access token>" }
  }]
}
```

## MCP execution and credentials

Only named MCP descriptors accept `execution`. Omit it or use `client` to run MCP in the gateway's hosted harness. In Web, this is **Hosted**; the default **Client** option runs MCP in the browser instead and sends ordinary function definitions. Choose **Provider** in Web to send `execution: "server"`. In this mode, the provider receives the native MCP configuration and connects to the supplied URL. Neither the browser nor the gateway discovers tools, loads MCP resources, or invokes that server for the turn. The URL must be reachable from the provider, not just from the browser or gateway.

Set `headers` inside the MCP descriptor on either route. These are MCP credentials, not inference or gateway sign-in credentials. The gateway's public MCP setup sends caller-supplied headers to the declared endpoint without per-endpoint header allowlists or credential storage. The existing outbound destination checks, redirect restrictions, and reserved transport-header restrictions still apply. Provider execution preserves the headers in the native Responses descriptor. Anthropic Messages can represent a single Bearer token as `authorization_token`, but cannot accept arbitrary headers or explicit approval policies other than `never`. If the selected provider route cannot represent the configuration, the gateway rejects it rather than running it locally or silently dropping credentials.

### Anthropic Messages

API Chat and Responses can mix named gateway-hosted and provider-executed MCP servers in one request. Set execution per descriptor, using `client` (or omitting it) for the gateway and `server` for Anthropic. The gateway sends only the generated function definitions for its own servers. For each provider server, it sends an `mcp_servers` entry and one `mcp_toolset`, mapping the allowlist and deferred loading into toolset configuration. Anthropic connects to those servers itself; the gateway does not invoke their tools. Provider execution requires an Anthropic-reachable HTTPS endpoint, supports tool calls only, and is not eligible for Anthropic zero data retention.

Send `anthropic-version` on either route. API Chat reads `providerMetadata.anthropic.max_tokens`, with `maxOutputTokens` as the fallback. Responses uses `max_output_tokens`. The gateway preserves this limit and native assistant history during internal hosted-tool rounds, including completed native MCP calls and signed thinking blocks.

Named provider MCP defaults to `mcp-client-2025-11-20`. On platforms that support pinned listings, explicitly send `anthropic-beta: mcp-client-2026-09-15` and keep that header on continuations. Preserve the returned assistant message metadata and provider-executed parts in API Chat, or the `mcp_list_tools` and completed `mcp_call` items in Responses inline history. For Anthropic-specific toolset options not represented by the portable descriptor (such as an explicitly pinned `tools` array or individual denylist overrides), use native Messages JSON or raw Anthropic options in API Chat instead of mixing raw and named provider MCP configuration.

Web resolves app-managed MCP credentials before building either request. It uses `descriptorHeaders` when supplied, otherwise it uses the existing per-endpoint headers from adapters that do not require a browser challenge. Challenge-only adapters must supply `descriptorHeaders` for Hosted or Provider execution. The Entra sample reuses the short-lived bearer exchanged for the exact endpoint. Manually supplied MCP bearer headers are forwarded as entered, independently of Gateway sign-in and inference credentials.

## Approval order

- **Responses:** The gateway emits a standard `mcp_approval_request` for both hosted and provider-native approvals. It shows the arguments, not a result. Continue with the original item and a matching `mcp_approval_response` in `input`; copy `response.metadata.streaik_mcp_approvals` into the top-level `streaik_mcp_approvals` request field. Keep the same caller, model and tool declarations. The hosted tool runs only on approval. For later history, include the completed `mcp_call`, with any matching receipt from `response.metadata.streaik_mcp_history` in the top-level `streaik_mcp_history` field. Do not include an old approval request or decision in later requests. A direct provider connection does not use the gateway proof fields.
- **API Chat:** A named MCP descriptor with `execution: "server"` asks for approval **before** execution if the provider requires it. Replay the provider-executed approval part once with the same input, tool metadata, decision and signature if supplied. The gateway sends the decision upstream and never runs that MCP tool locally. Gateway-hosted MCP and legacy native MCP declared in API Chat provider metadata retain result review: they execute **before** review and emit the input and completed output. Replay the same tool call and approval IDs, input, decision, signature if supplied, and the **client's output** on approval. Keep the same conversation ID, model, tool declarations, and caller in either case. Denying a result review does not undo execution. Ordinary client tool approvals are unchanged.

The gateway keeps no per-approval records or cached outputs. Configure all replicas with the same ASP.NET Core Data Protection application name and key ring; BYOK continuations must also present the same provider credential. Native Responses proof issuance requires Data Protection (enabled by `AddStreaikReferences` or `AddStreaikHostedTools`). Pending execution proofs expire after one hour; completed history receipts remain verifiable while their keys are retained. A valid proof does not indicate whether a call was already dispatched. Clients must submit each active decision at most once and avoid automatically retrying an operation after an ambiguous failure.
