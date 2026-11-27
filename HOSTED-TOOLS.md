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

**Responses** uses a matching `name` and `type` to identify a Streaik descriptor. Ordinary provider-native entries such as `{ "type": "mcp", ... }` without the name still follow their existing provider route. The gateway removes hosted configuration before inference and sends only the exposed function declarations and relevant instructions.

```json
{
  "tools": [{
    "type": "mcp",
    "name": "mcp",
    "server_label": "inventory",
    "server_url": "https://inventory.example.com/mcp"
  }]
}
```

## Approval order

- **Responses:** The gateway emits a standard `mcp_approval_request` for both hosted and provider-native approvals. It shows the arguments, not a result. Continue with the original item and a matching `mcp_approval_response` in `input`; copy `response.metadata.streaik_mcp_approvals` into the top-level `streaik_mcp_approvals` request field. Keep the same caller, model and tool declarations. The hosted tool runs only on approval. For later history, include the completed `mcp_call`, with any matching receipt from `response.metadata.streaik_mcp_history` in the top-level `streaik_mcp_history` field. Do not include an old approval request or decision in later requests. A direct provider connection does not use the gateway proof fields.
- **API Chat:** A hosted MCP tool that requires result review executes **before** approval. A provider-native MCP approval also becomes a result review on this route. Both forms emit provider-executed tool input and a `tool-approval-request` with the input and completed output. Replay the same tool call and approval IDs, input, decision, signature if supplied, and the **client's output** on approval. Use the same conversation ID, model, tool declaration and caller. Gateway forwards the approved client-supplied value and ignores the output on denial. Neither decision reruns the tool, and denial does not undo execution. Ordinary client tool approvals are unchanged.

The gateway keeps no per-approval records or cached outputs. Configure all replicas with the same ASP.NET Core Data Protection application name and key ring; BYOK continuations must also present the same provider credential. Native Responses proof issuance requires Data Protection (enabled by `AddStreaikReferences` or `AddStreaikHostedTools`). Pending execution proofs expire after one hour; completed history receipts remain verifiable while their keys are retained. A valid proof does not indicate whether a call was already dispatched. Clients must submit each active decision at most once and avoid automatically retrying an operation after an ambiguous failure.
