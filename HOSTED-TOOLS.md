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

- **Responses:** Only a real upstream `mcp_approval_request` is sent to the client. It contains the input, and no local tool is dispatched. Submit a native `mcp_approval_response` with the original approval ID, model, tools and authenticated identity. The provider receives the decision once and executes only on approval. Do not include an old decision in later requests.
- **Direct API Chat:** Explicit `require_approval: "always"` on a named hosted MCP tool asks Gateway to execute once and review the result before sending anything to the model. A real upstream MCP approval also becomes a result review on this route. Both forms emit a provider-executed tool input and an approval request with the completed output. Resubmit the tool call and approval IDs, input, decision, signature if supplied, and the **client's output** on approval, with the same tool declaration, caller, model and conversation. The BYOK sample requires the same provider credential on continuation. Gateway does not cache the output; it forwards the approved client-supplied value and ignores the output on denial. Neither decision reruns the call. Denial does not undo execution.
- **Other local approval checks:** Outside the explicit hosted API Chat case, a local policy requiring approval still blocks the call before execution if no genuine upstream approval exists. Ordinary client tools on API Chat retain their own approval flow. API Chat requests can declare provider-native MCP in the selected provider's metadata, separately from named hosted descriptors under `inputSchema`.

There is no fixed approval expiry. The default stores are bounded and instance-local: continuations need affinity, unknown and consumed native decisions fail closed, and full stores reject new approvals instead of dropping replay guards. They do not provide cross-instance or cross-restart idempotency.
