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

## Approval order

- **Responses:** The gateway emits a standard `mcp_approval_request` for both hosted and provider-native approvals. It shows the arguments, not a result. Continue with the original item and a matching `mcp_approval_response` in `input`; copy `response.metadata.streaik_mcp_approvals` into the top-level `streaik_mcp_approvals` request field. Keep the same caller, model and tool declarations. The hosted tool runs only on approval. For later history, include the completed `mcp_call`, with any matching receipt from `response.metadata.streaik_mcp_history` in the top-level `streaik_mcp_history` field. Do not include an old approval request or decision in later requests. A direct provider connection does not use the gateway proof fields.
- **API Chat:** A named MCP descriptor with `execution: "server"` asks for approval **before** execution if the provider requires it. Replay the provider-executed approval part once with the same input, tool metadata, decision and signature if supplied. The gateway sends the decision upstream and never runs that MCP tool locally. Gateway-hosted MCP and legacy native MCP declared in API Chat provider metadata retain result review: they execute **before** review and emit the input and completed output. Replay the same tool call and approval IDs, input, decision, signature if supplied, and the **client's output** on approval. Keep the same conversation ID, model, tool declarations, and caller in either case. Denying a result review does not undo execution. Ordinary client tool approvals are unchanged.

The gateway keeps no per-approval records or cached outputs. Configure all replicas with the same ASP.NET Core Data Protection application name and key ring; BYOK continuations must also present the same provider credential. Native Responses proof issuance requires Data Protection (enabled by `AddStreaikReferences` or `AddStreaikHostedTools`). Pending execution proofs expire after one hour; completed history receipts remain verifiable while their keys are retained. A valid proof does not indicate whether a call was already dispatched. Clients must submit each active decision at most once and avoid automatically retrying an operation after an ambiguous failure.
