# Semantic Gateway MCP server: code tour

An ASP.NET Core app of about a dozen small files. Read [Program.cs](Program.cs) first: it wires
everything in the order a request experiences it.

| File | What it does |
|---|---|
| [Program.cs](Program.cs) | Configuration, JWT validation, MCP server, portal API endpoints, static portal. |
| [Settings.cs](Settings.cs) | The settings sections: `Fabric`, `FabricIq`, `AzureOpenAI`, `Auth`. |
| [Identity/AppUsers.cs](Identity/AppUsers.cs) | Maps the token's `sub` to the user key that RLS matches. Stand-in for your entitlement service. |
| [Identity/DevIdentityProvider.cs](Identity/DevIdentityProvider.cs) | **Demo only.** A tiny token issuer for the synthetic users. Replace with your identity provider (`Auth:Mode = Oidc`). |
| [Fabric/PowerBiAppIdentity.cs](Fabric/PowerBiAppIdentity.cs) | The ISV's service principal (certificate) that calls Power BI for every user. |
| [Fabric/DaxQueryClient.cs](Fabric/DaxQueryClient.cs) | `executeDaxQueries` with the fixed RLS role and `customData` (or `effectiveUsername`). This replaces Fabric IQ `ExecuteQuery`. |
| [Fabric/ArrowResults.cs](Fabric/ArrowResults.cs) | Reads the Apache Arrow response, including error rowsets. |
| [Fabric/FabricIqSchemaClient.cs](Fabric/FabricIqSchemaClient.cs) | MCP **client** to Fabric IQ: calls `GetSemanticModelSchema` as the metadata account. Also the one-time `sign-in`. |
| [Fabric/SchemaCache.cs](Fabric/SchemaCache.cs) | Caches the schema, removes excluded tables and keeps a snapshot on disk. |
| [Fabric/EmbedTokenService.cs](Fabric/EmbedTokenService.cs) | Power BI Embedded token with the same role and key, for the comparison page. |
| [Tools/SemanticModelTools.cs](Tools/SemanticModelTools.cs) | The three tools and their contract, shared by MCP and the chat agent. |
| [Tools/McpToolsEndpoint.cs](Tools/McpToolsEndpoint.cs) | MCP **server** registration with the official SDK: list tools, call tools as the token's user. |
| [Chat/ChatAgent.cs](Chat/ChatAgent.cs) | The portal agent: an Azure OpenAI Responses API tool loop over the same tools. |

## HTTP endpoints

| Endpoint | Auth | Purpose |
|---|---|---|
| `POST /mcp` | Bearer | MCP server (Streamable HTTP, stateless) |
| `GET /.well-known/oauth-protected-resource` | none | RFC 9728 metadata for MCP clients |
| `GET /api/config` | none | Portal settings, demo users, suggestions |
| `GET /api/me` | Bearer | The signed-in user, user key, role and identity mode |
| `POST /api/chat` | Bearer | Portal chat; streams progress as NDJSON |
| `GET /api/embed` | Bearer | Embed URL and token for the comparison report |
| `GET /api/parity` | Bearer | The same numbers as the report, through the gateway |
| `POST /dev-idp/token` | none | Demo sign-in (development mode only) |

## Configuration

`appsettings.json` has placeholders. Put your values in `appsettings.Local.json` (git-ignored);
environment variables and command-line arguments override both.

| Setting | Meaning |
|---|---|
| `Fabric:TenantId`, `WorkspaceId`, `SemanticModelId` | Where the model lives |
| `Fabric:ReportId` | Optional report for the Embedded comparison |
| `Fabric:RlsRole` | The role sent with every query, e.g. `ExternalAppScope` |
| `Fabric:IdentityMode` | `CustomData` (recommended) or `EffectiveUsername` (see [docs/power-bi-embedded.md](../../docs/power-bi-embedded.md)) |
| `Fabric:ServicePrincipal` | `ClientId` plus `CertificateThumbprint` (Windows store) or `CertificatePemPath` and `PrivateKeyPemPath` |
| `FabricIq:ClientId`, `LoginHint` | Public client and account for the metadata sign-in |
| `FabricIq:ExcludeTables` | Tables hidden from the schema, e.g. `User Access` |
| `FabricIq:SchemaCacheMinutes` | Schema cache lifetime |
| `AzureOpenAI:Endpoint`, `Deployment` | The portal agent's model |
| `Auth:Mode` | `Development` (built-in demo issuer) or `Oidc` |
| `Auth:Issuer`, `Audience`, `Authority` | Token validation; `Authority` for `Oidc` |
| `Auth:UsersFile` | The user-to-key mapping, default `../../samples/users.json` |

## Commands

```powershell
dotnet run -- sign-in    # once: sign in the Fabric IQ metadata account (opens a browser)
dotnet run               # start the gateway at http://localhost:5187
```

Local state (never in the repository): the MSAL token cache `isv-semantic-gateway-fabric-iq`, and
`%LOCALAPPDATA%\isv-semantic-gateway\` for the signed-in account record and the schema snapshot.
