# Relationship to Power BI Embedded (app owns data)

If you already embed Power BI with **app owns data**, the gateway reuses most of your design. It applies
the same idea (your backend tells Power BI who the user is, and the model's RLS enforces it) to
AI-generated DAX instead of report visuals.

## What carries over, what differs, what is not used

| Aspect | Power BI Embedded, app owns data | The semantic gateway | Relationship |
|---|---|---|---|
| End-user sign-in | Your app, any identity provider | Your app, any identity provider; MCP clients present your app's tokens | **Same** |
| Entitlements | Your backend maps the user to what they may see | The same mapping, e.g. `erin` to `app-user-pairs` | **Same** |
| Identity that calls Power BI | Service principal (or master user) | The same service principal, with a certificate | **Same** |
| Workspace role of that identity | Member or Admin is typical | **Admin**. Only Admins may pass `roles` to `executeDaxQueries`. | Differs |
| How the user reaches RLS | `GenerateToken` effective identity: `username`, `roles`, `customData`, `datasets` | Every `executeDaxQueries` call: `roles`, `customData` | **Same idea, different carrier** |
| Where RLS is enforced | A role in the semantic model | The same role | **Same**, if it reads `CUSTOMDATA()` |
| Token in the browser | Embed token | None from Microsoft; the browser holds only your app's token | **Not used** |
| Rendering | Power BI JavaScript SDK in an iframe | Your UI shows the answer and rows; MCP clients show their own UI | **Not used** |
| Who writes queries | Report visuals, designed in advance | The LLM, per question | **Differs** |
| Semantic context for AI | Not needed | Fabric IQ schema, read by a metadata account | **New** |
| Report, page and visual filters | Apply to visuals | Do not apply to AI queries | **Differs**: put security in RLS, never in report filters |

## The two calls, side by side

Both are made by the same app identity, for the same user, with the same role and key.

```text
Embedded:  POST /v1.0/myorg/GenerateToken
           { reports: [{ id }], datasets: [{ id }],
             identities: [{ username: "app-user-pairs", roles: ["ExternalAppScope"],
                            customData: "app-user-pairs", datasets: ["<model id>"] }] }

Gateway:   POST /v1.0/myorg/groups/{workspace}/datasets/{model}/executeDaxQueries
           { query: "<DAX from the model or MCP client>",
             roles: ["ExternalAppScope"], customData: "app-user-pairs" }
```

In code:
[EmbedTokenService.cs](../src/SemanticGateway/Fabric/EmbedTokenService.cs) and
[DaxQueryClient.cs](../src/SemanticGateway/Fabric/DaxQueryClient.cs).

## Measured on 29 September 2026

| Test | Result |
|---|---|
| Embedded report for `erin` with `customData` identity, `CUSTOMDATA()` role | A/Home 250.00 (2), B/Auto 900.00 (1), total 1,150.00 (3) |
| Parity: the report visual's exported data vs the gateway's rows for `erin` | **Match** |
| Embedded report with `username` = `app-user-pairs` and a `USERNAME()` role | Filtered correctly: the same three rows |
| `executeDaxQueries` with `effectiveUsername` = `app-user-pairs` and the same `USERNAME()` role | **Rejected** for all six users: *"Either the database ... does not exist, or you do not have permissions to access it."* |
| Control: `effectiveUsername` = a real directory user of the tenant | The identity resolved, then: *"You are not a member of any of the roles specified ..."* |

What this means:

- In Embedded, `username` can be any string your app chooses, and `USERNAME()` returns it.
- In `executeDaxQueries`, `effectiveUsername` is **impersonation of a real directory identity**. It
  cannot carry an arbitrary application key, and the impersonated user must be a member of the role.
- **`CUSTOMDATA()` works in both paths** with an arbitrary application key. It is the portable choice.

## Migrating an existing Embedded solution

1. **Check your RLS roles.** If they read `CUSTOMDATA()`, reuse them as they are. If they read
   `USERNAME()` or `USERPRINCIPALNAME()` with application-level names, add a role with the same logic
   that reads `CUSTOMDATA()`, and send `customData` from both `GenerateToken` and the gateway. One role
   then serves both paths.
2. **Make the service principal a workspace Admin** of the workspace that holds the model. That lets it
   pass `roles` to `executeDaxQueries`. The gateway must then always send a role.
3. **Move any security that lives in report filters into RLS.** AI queries never see report filters.
4. **Plan capacity and throttling.** AI queries run on the same capacity as the reports.
   `executeDaxQueries` allows 120 query requests per minute per caller, and every user's AI queries use
   the one app identity.
5. **Reuse your entitlement service.** The gateway's `AppUserDirectory` is a stand-in for it.

## What does not transfer

- An embed token cannot call `executeDaxQueries` or Fabric IQ; it only authorizes embedded content.
- Fabric IQ cannot act as your application user. Use it for schema through a metadata account, and run
  data queries through the gateway.
