# Connecting MCP clients

The gateway's `/mcp` endpoint is a standard MCP server (Streamable HTTP, stateless) built with the
official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk). Any MCP client that can send
a bearer token can use it. Every call runs as the user the token belongs to, so two people using the same
client see different rows.

| | |
|---|---|
| Endpoint | `http://localhost:5187/mcp` (your hosted URL in production) |
| Tools | `get_semantic_model_schema`, `search_values`, `execute_dax`, all read-only |
| Authentication | `Authorization: Bearer <token issued for the gateway>` |
| Discovery | `401` + `WWW-Authenticate: Bearer resource_metadata=".../.well-known/oauth-protected-resource/mcp"` |

## 1. Get a token for a user

In development mode the gateway has a built-in issuer for the synthetic users in
[samples/users.json](../samples/users.json):

```powershell
$token = (Invoke-RestMethod -Method Post http://localhost:5187/dev-idp/token `
    -ContentType 'application/json' `
    -Body '{"username":"carol","password":"synthetic-only"}').access_token
```

```bash
curl -s -X POST http://localhost:5187/dev-idp/token -d username=carol -d password=synthetic-only
```

Tokens last 60 minutes and stop working when the gateway restarts, because the development signing key
lives in memory. Treat them like passwords: they are the user's identity.

## 2. GitHub Copilot CLI (tested)

Create a config file from
[samples/mcp-clients/copilot-cli.mcp-config.example.json](../samples/mcp-clients/copilot-cli.mcp-config.example.json)
with your token, then:

```powershell
copilot --additional-mcp-config "@C:\path\to\mcp-config.json" --allow-tool isv-gateway `
  -p "Using the isv-gateway tools, get the schema and show my total amount and activity count by customer and product."
```

What happened in the live run for `carol`:

1. `tools/list` returned the three tools.
2. `get_semantic_model_schema` returned the Fabric IQ schema through the gateway.
3. The first `execute_dax` attempt had a DAX error. The error came back as a tool error, and Copilot
   fixed the query itself.
4. The second attempt returned only Carol's rows: A/Auto 100 (2 activities) and A/Home 250 (2).

To make the server permanent, add the same entry to `~/.copilot/mcp-config.json`.

## 3. VS Code (configuration example, not tested here)

`.vscode/mcp.json` can prompt for the token instead of storing it:

```json
{
  "inputs": [
    { "type": "promptString", "id": "gateway-token", "description": "Gateway bearer token", "password": true }
  ],
  "servers": {
    "isv-gateway": {
      "type": "http",
      "url": "http://localhost:5187/mcp",
      "headers": { "Authorization": "Bearer ${input:gateway-token}" }
    }
  }
}
```

## 4. Other clients

Any client that supports Streamable HTTP servers and either custom headers or the MCP OAuth flow works
the same way: point it at `/mcp` and give it a token for the user.

## 5. Production: let clients sign users in

Pre-issued tokens are fine for a demo. For real customers, let MCP clients run the OAuth flow described
in the [MCP authorization specification](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization):

1. The client calls `/mcp` without a token and gets `401` with `resource_metadata`.
2. It reads `/.well-known/oauth-protected-resource` and finds your identity provider in
   `authorization_servers`.
3. It signs the user in with the authorization code flow and PKCE, asking for a token for the gateway
   (`resource` = the gateway URL).
4. It calls `/mcp` with that token. The gateway validates issuer and audience, then maps the user as
   usual.

On the gateway, set `Auth:Mode` to `Oidc`, `Auth:Authority` to your provider and `Auth:Audience` to the
audience your provider puts in gateway tokens. Your provider must support the discovery and client
registration options your customers' MCP clients use (client ID metadata documents, dynamic client
registration, or pre-registered clients). Check this per client and per provider.

## Rules the gateway keeps for every client

- Identity comes only from the validated token. Tool arguments cannot change the user, role or key.
- Only tokens issued for the gateway are accepted, and no token is passed through to Microsoft services.
- The same RLS role and key apply whether the question came from the portal or from an MCP client.
