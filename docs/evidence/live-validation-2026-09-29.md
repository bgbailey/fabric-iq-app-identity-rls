# Live validation, 29 September 2026

Everything here ran against real services with **synthetic data only**. Tenant, workspace, model and
application IDs are omitted.

## Environment

| Part | Version or size |
|---|---|
| Fabric capacity | F8, one workspace |
| Semantic model | This repository's [model](../../model/): 4 tables, 6 activity rows, roles `ExternalAppScope` (`CUSTOMDATA()`), `ExternalAppUsername` (`USERNAME()`), `MetadataOnly` |
| Report | This repository's one-visual PBIR report |
| Gateway | [src/SemanticGateway](../../src/SemanticGateway/) on .NET 8, local, `http://localhost:5187` |
| MCP SDK | ModelContextProtocol 2.2.0 (server and Fabric IQ client) |
| LLM | GPT-5.4 (Azure OpenAI, Responses API, `store: false`, reasoning effort low) |
| External MCP client | GitHub Copilot CLI 1.0.90 |

## 1. Authentication and MCP protocol

| Check | Result |
|---|---|
| `/api/me` and `/mcp` without a token | `401`, `WWW-Authenticate: Bearer resource_metadata="http://localhost:5187/.well-known/oauth-protected-resource/mcp"` |
| Protected resource metadata | `{"resource":"http://localhost:5187/mcp","authorization_servers":["http://localhost:5187/dev-idp"],"bearer_methods_supported":["header"],...}` |
| Wrong password at the development issuer | `400 invalid_grant` |
| `initialize` | Protocol `2025-06-18`, server `isv-semantic-gateway 1.0.0` |
| `tools/list` | 3 tools, all `readOnlyHint: true` |

## 2. Fabric IQ schema

| Check | Result |
|---|---|
| `get_semantic_model_schema`, first call | 3.4 s. The gateway called Fabric IQ MCP `GetSemanticModelSchema` as the metadata account through the MCP C# SDK client. |
| Tables returned | `Activity`, `Date`, `Scope`. `User Access` was removed by `ExcludeTables`; IQ also omits hidden columns. |
| Relationships | 2 |
| Later calls | Served from the cache |

## 3. Row-level security through `execute_dax`

The same query for every user, sent by an MCP client:

```dax
EVALUATE ROW("Total Amount", [Total Amount], "Activity Count", [Activity Count])
```

| User | User key | Grants | Result |
|---|---|---|---|
| alice | `app-user-A1` | A/Home | 250, 2 |
| bob | `app-user-A2` | A/Auto | 100, 2 |
| carol | `app-user-A3` | A/Home, A/Auto | 350, 4 |
| dan | `app-user-B1` | B/Home | 700, 1 |
| erin | `app-user-pairs` | A/Home, B/Auto | 1,150, 3 |
| frank | `app-user-none` | none | no rows |

Every call also carried an injected argument `userKey: "app-user-B1"`; it was ignored.

| Probe (as dan) | Result |
|---|---|
| `CALCULATE([Total Amount], ALL('Scope'))` and `CALCULATE([Total Amount], REMOVEFILTERS())` | 700 and 700: no widening |
| `search_values` on `Scope[Product]` for "o" | `["Home"]` only |
| `EVALUATE ROW("who", CUSTOMDATA())` | Rejected by the gateway's query check |
| `EVALUATE 'User Access'` | Rejected: excluded table |
| DAX referencing a missing measure | Tool error with the service's message, which the client can act on |
| Erin's breakdown by customer and product | A/Home 250 (2), B/Auto 900 (1) |

## 4. Portal chat agent

| Question | User | Tool calls | Model calls | Time | Answer |
|---|---|---|---|---|---|
| Break down my activity by customer and product | erin | schema, `execute_dax` | 3 | about 25 s | A/Home 250 (2), B/Auto 900 (1) |
| What is Customer A's Home activity? | dan | schema, `search_values` ×2 | 3 | about 30 s | Customer A is not visible to you or does not exist |

Before the agent instructions were tightened, the second question used all 6 model calls and retried
the search several times; the answer was still correct and still scoped.

### After the latency fix (same afternoon)

Two changes: the Azure OpenAI token is cached instead of fetched for every model call, and the cached
schema is given to the portal agent up front instead of costing a model round trip. `search_values` also
now returns the values the user can see when nothing matches, so *Customer B* resolves to the stored
value *B*. Timed runs, measured at the client, with the gateway warm:

| Question | User | Time | Answer |
|---|---|---|---|
| What is my total amount and activity count? | alice | 10.7 s | 250 and 2 |
| Break down my activity by customer and product | erin | 9.5 s (2 model calls) | A/Home 250 (2), B/Auto 900 (1) |
| What is Customer B's Home activity? | dan | 15.2 s | 700 across 1 activity |
| What is Customer B's Home activity? | erin | 16.1 s | No visible activity for this user |
| What is Customer A's Home activity? | dan | 12.1 s | Not visible to this user |

Before the change, portal questions took 25 to 92 s. MCP clients are not affected by this change: they run
their own model and call the gateway's tools directly.

## 5. GitHub Copilot CLI as the customer's MCP client

Signed in as carol with a development token in the client's MCP configuration: `tools/list`, schema,
one `execute_dax` that failed with a DAX error (returned as a tool error), and one corrected
`execute_dax`. Result: A/Auto 100 (2), A/Home 250 (2). About 31 s end to end.

## 6. Power BI Embedded comparison

| Check | Result |
|---|---|
| `GenerateToken` for alice, erin and frank with `username`, `roles`, `customData` | `200`, 10-minute embed tokens |
| Embedded report for erin | A/Home 250.00 (2), B/Auto 900.00 (1), total 1,150.00 (3) |
| Parity: exported visual data vs the gateway's `/api/parity` rows for erin | **Match** |

## 7. `effectiveUsername` experiment

The gateway was started a second time with `Fabric:IdentityMode=EffectiveUsername` and
`Fabric:RlsRole=ExternalAppUsername`, a role that reads `USERNAME()`.

| Check | Result |
|---|---|
| `executeDaxQueries` with `effectiveUsername` = application key, all six users | Rejected: *"Either the database ... does not exist, or you do not have permissions to access it."* |
| Control: `effectiveUsername` = a real directory user | Identity resolved, then: *"You are not a member of any of the roles specified ..."* |
| Embedded with `username` = `app-user-pairs` and the `USERNAME()` role | Filtered correctly (erin's three rows) |

Conclusion: use `customData` with a `CUSTOMDATA()` role for non-directory users. See
[power-bi-embedded.md](../power-bi-embedded.md).

## Earlier evidence

On 28 September 2026 the deterministic harness in [dev/rls-harness](../../dev/rls-harness/) sent the same
unfiltered and adversarial DAX for every synthetic user and passed 42 of 42 live checks, covering direct
table scans, `ALL`, `REMOVEFILTERS`, denied scopes and entitlement enumeration.

## Not tested

- A real OIDC identity provider, and MCP clients running the interactive OAuth flow.
- MCP clients other than GitHub Copilot CLI.
- Throughput and concurrency: `executeDaxQueries` allows 120 query requests per minute per caller.
- A least-privilege metadata account, and the `MetadataOnly` role applied to it.
- Hosting in Azure with a managed identity and Key Vault.
