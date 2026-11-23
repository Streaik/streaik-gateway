# streaik-gateway

A multi-provider **.NET AI gateway** exposing normalized endpoints for models, media, skills, agents and MCP capabilities.

No smart routing fairy. No shiny admin portal. No hidden logic maze. No code execution.
Just provider capabilities flattened, normalized hard and exposed through open contracts.
Stateless. Boring on purpose.

Access 150k+ models and provider-native capabilities from your favorite client.

**Documentation:** https://ai.streaik.net/scalar  
**Providers:** https://github.com/Streaik/streaik-providers  
**API:** https://ai.streaik.net

## Quick start

```bash
curl "https://ai.streaik.net/v1/chat/completions" \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer $OPENAI_API_KEY" \
  -d '{
    "model": "openai/gpt-5.4-mini",
    "messages": [
      { "role": "user", "content": "Say hi" }
    ]
  }'
```

See the [API documentation](https://ai.streaik.net/scalar) for endpoints, authentication, request schemas and examples.

## Run standalone on Windows

Run the gateway standalone on your own Windows machine. Download the latest standalone Windows x64 build:
[streaik-windows-x64.zip](https://github.com/Streaik/streaik-gateway/releases/download/latest/streaik-windows-x64.zip)

Provider credentials are loaded from: `%LOCALAPPDATA%\streaik\headers.json`  
Example: `{"X-OpenAI-Key":"sk-...","X-Anthropic-Key":"sk-ant-..."}`

Start `streaik.Windows.exe` and use the same APIs locally at `http://localhost:5000`.

