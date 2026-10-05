# From an Embedded report to AI: one user, the same RLS policy

**Takeaway:** keep authorization in your backend and semantic model. Add an AI query path that carries
the same trusted user key and role. Do not give the AI an embed token or ask it to enforce customer access.

## 1. What is off the shelf, and what do we implement?

![Reuse the platform; add application identity binding](images/reuse-vs-custom.png)

| Component | Reused | Application-specific work in this repository |
|---|---|---|
| MCP protocol and HTTP server/client | Official [MCP C# SDK](https://github.com/modelcontextprotocol/csharp-sdk). | Register three tool handlers and resolve each request's authenticated application user. |
| Model metadata | Native **Fabric IQ MCP** `GetSemanticModelSchema`. | Cache/filter the returned schema for the application. A delegated metadata account makes this call. |
| Data query execution | **Power BI REST** `executeDaxQueries` and the semantic engine. | Attach the server-selected role and user key, submit DAX, decode results, and return explicit errors. |
| Report embedding | **Power BI REST** `GenerateToken` and the Power BI JavaScript SDK. | Supply the same application's effective identity and return the scoped embed token to the browser. |
| Authorization | Existing semantic-model **RLS**. | Maintain trusted entitlement mapping and a compatible role; the LLM does not implement it. |

The custom gateway is a new **MCP server implemented with the standard SDK**, not a fork of Microsoft's
Fabric IQ MCP server. Its schema tool wraps native IQ; its two data tools use Power BI REST instead
of IQ `ExecuteQuery` or `ValueSearch`. All three share the same authenticated application-user boundary.

| Custom tool | Underlying call | Authorization carrier |
|---|---|---|
| `get_semantic_model_schema` | Native IQ `GetSemanticModelSchema(artifactId)` | Delegated metadata account; no external-user key. |
| `search_values` | Power BI `executeDaxQueries` | Service-principal OAuth bearer + fixed `roles` + user-key `customData`. |
| `execute_dax` | The same Power BI `executeDaxQueries` client | The same identity construction as scoped value search. |

**Why not use native IQ for the data calls?** Its tools run under the signed-in Fabric identity.
That is not automatically the external user's application key. This adapter retains application-owned
sign-in and grants rather than making a metadata account the data-query identity.

## 2. Follow Erin through the two delivery paths

Erin's validated application token resolves to `app-user-pairs` on the server. The configured role is
`ExternalAppScope`; its exact grants authorize A/Home and B/Auto. The model is the same in both paths.

![Same RLS, two carriers](images/same-rls-two-carriers.png)

### Existing Embedded call

The backend authenticates to Power BI as the service principal and calls `GenerateToken`.
The relevant JSON is below; placeholder resource IDs are not runnable configuration:

```json
{
  "datasets": [{ "id": "<same-model-id>" }],
  "reports": [{ "id": "<report-id>" }],
  "identities": [{
    "username": "app-user-pairs",
    "roles": ["ExternalAppScope"],
    "customData": "app-user-pairs",
    "datasets": ["<same-model-id>"]
  }]
}
```

`GenerateToken` scopes access; it does not return analytical rows. The browser receives a **distinct
embed token**, and report visuals query the model under its effective identity.

### Added AI query

The AI writes DAX. The backend authenticates with the **same service-principal component**, selects
the same model, and attaches the role/key to each `executeDaxQueries` request:

```json
{
  "query": "EVALUATE SUMMARIZECOLUMNS('Scope'[Customer], 'Scope'[Product], \"Total Amount\", [Total Amount], \"Activity Count\", [Activity Count])",
  "roles": ["ExternalAppScope"],
  "customData": "app-user-pairs"
}
```

This response contains analytical data, not an embed token. The snippets show the identity fields;
the production client also sets query timeout and row limits.

**Both evaluate through the model's `CUSTOMDATA()` role.** `customData` is request/effective-identity
content, not a claim added to the service principal's OAuth token. The application token, backend
Power BI token, and browser embed token are different credentials.

## 3. The code overlap is concrete

| Responsibility | Shared implementation | Where the paths differ |
|---|---|---|
| Resolve the user | [`AppUserDirectory.Resolve`](../src/SemanticGateway/Identity/AppUsers.cs) maps validated `sub` to `AppUser.UserKey`; unknown users fail. | Both `/api/embed` and `/api/chat` resolve the authenticated user. MCP requests do the same. |
| Authenticate the backend | [`PowerBiAppIdentity`](../src/SemanticGateway/Fabric/PowerBiAppIdentity.cs) uses certificate credentials and obtains the Power BI OAuth token. | Both `EmbedTokenService` and `DaxQueryClient` receive this component. |
| Construct RLS context | Both use `user.UserKey` and configured `fabric.RlsRole`. | [Embed identity](../src/SemanticGateway/Fabric/EmbedTokenService.cs): `Username`, `Roles`, `CustomData`, `Datasets`. [Query request](../src/SemanticGateway/Fabric/DaxQueryClient.cs): `Query`, `Roles`, `CustomData`. |
| Enforce grants | [`ExternalAppScope`](../model/Synthetic.SemanticModel/definition/roles/ExternalAppScope.tmdl) matches the user key to exact scope grants. | Report visuals and AI DAX can differ; the role is still the row-access boundary. |
| Expose AI tools | [`SemanticModelTools`](../src/SemanticGateway/Tools/SemanticModelTools.cs) is shared by portal chat and MCP. | Portal invokes it in-process; [`McpToolsEndpoint`](../src/SemanticGateway/Tools/McpToolsEndpoint.cs) exposes it through the SDK. |

The relevant production construction in both files is the same:

```csharp
Roles: [fabric.RlsRole],
CustomData: fabric.IdentityMode == IdentityMode.CustomData ? user.UserKey : null
```

These fields come from trusted server state, not the user's question or tool arguments. An adopter
replaces the synthetic JSON directory with their existing entitlement lookup; this repository does
not deliver that production adapter.

## 4. Security overlap, without overclaiming

| Question | Answer |
|---|---|
| Who authenticates the external user? | Your application identity provider; development tokens stand in for it in the demo. |
| Who chooses their grants? | Your backend mapping and model's entitlement policy. Neither the client nor the AI chooses a different key/role/model. |
| Who enforces rows? | The semantic-model engine in both paths, not report filters or prompt instructions. |
| Can AI DAX remove RLS with `ALL()`? | The observed unfiltered queries remained scoped. Removing a query filter does not remove the configured RLS policy. |
| What remains new? | The custom tool adapter, delegated metadata dependency, AI orchestration, result handling, and operational/answer-quality evaluation. |
| Is this a drop-in identity migration? | Only after qualification. `CUSTOMDATA()` is the demonstrated route; opaque `USERNAME()`/UPN rules, profiles, and SSO may need different integration. |

The service principal must be workspace **Admin** to select roles on `executeDaxQueries`; do not assume
every existing Embedded permission assignment is sufficient. Keep the certificate server-side and
always attach the intended role.

RLS constrains data execution; it does not certify the AI's prose or query intent. Shared schema is
not per-user OLS. The model, entitlement mapping, and API identity contract must remain correct.

## 5. A short customer walkthrough

1. Sign in as Erin: show the report and AI breakdown, restricted to A/Home and B/Auto.
2. Point to the shared user key/role, then the two request payloads: the carrier changed, not the grants.
3. Switch to Dan and request Customer A: demonstrate that the engine does not return its rows.
4. Switch to Frank: demonstrate valid empty data, not a failed request disguised as no access.

The dated [live evidence](evidence/live-validation-2026-10-05.md) records these grants, unfiltered
engine probes, fresh AI queries, and report aggregate agreement. The aggregate indicator is a supporting
cross-check, not certification of all answers or identical underlying rows.

## Sources and evidence gaps / still thin

Current API contracts: [native Fabric IQ MCP](https://learn.microsoft.com/en-us/fabric/iq/connectors/fabric-iq-mcp),
[Arrow query REST](https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/execute-dax-queries-in-group),
[GenerateToken](https://learn.microsoft.com/en-us/rest/api/power-bi/embed-token/generate-token),
and [app-owns-data token flow](https://learn.microsoft.com/en-us/power-bi/developer/embedded/embed-tokens).
Research was performed on 2026-10-05; the [diagram sources/review](diagrams/README.md) retain the view boundaries.

Production OIDC/interactive OAuth, customer models, profiles/SSO, shared-metadata disclosure,
hosting, and scale remain qualification work. No new capability is implied by the simpler diagrams.
The [detailed architecture](images/shared-rls-architecture.png) remains available for technical drill-down.
