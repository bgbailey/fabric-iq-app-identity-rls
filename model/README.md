# Synthetic semantic model and report

Everything here is synthetic. The data lives in inline M partitions, so there are no data sources to
connect.

## Model: `Synthetic.SemanticModel` (TMDL)

| Table | Rows | Purpose |
|---|---|---|
| `Scope` | 4 | Customer/product pairs: A/Home, A/Auto, B/Home, B/Auto. The unit of authorization. |
| `Activity` | 6 | Facts, with measures `Total Amount` and `Activity Count`. Unrestricted total 1,950. |
| `Date` | 31 | A neutral January 2026 calendar. |
| `User Access` | 7 | Hidden mapping: user key → granted scope. Read only by the RLS roles. |

Relationships run one way from `Scope` and `Date` to `Activity`, so filtering `Scope` filters the facts.

## Roles

| Role | Reads | Use |
|---|---|---|
| `ExternalAppScope` | `CUSTOMDATA()` | **The pattern.** The gateway sends `roles: ["ExternalAppScope"]` and `customData: <user key>`. It also works in Power BI Embedded with `customData` in the effective identity. |
| `ExternalAppUsername` | `USERNAME()` | The same logic, reading `USERNAME()`. It works for Embedded with `username` = user key, but `executeDaxQueries` rejects a non-directory `effectiveUsername`. Kept to demonstrate that difference; see [docs/power-bi-embedded.md](../docs/power-bi-embedded.md). |
| `MetadataOnly` | nothing | Denies every business row. Intended for a metadata account that should read schema but never data (not verified here). |

Grants are exact pairs, compared case-sensitively with `EXACT`. A missing, empty or unknown key sees
nothing, and `User Access` has its own filter so nobody can list other users' grants.

## Expected results

[fixtures.json](fixtures.json) lists every activity and every user's grants and totals. The live checks
in [docs/evidence](../docs/evidence/) and the harness in [dev/rls-harness](../dev/rls-harness/) compare
against it.

| User key | Grants | Total | Activities |
|---|---|---|---|
| `app-user-A1` | A/Home | 250 | 2 |
| `app-user-A2` | A/Auto | 100 | 2 |
| `app-user-A3` | A/Home, A/Auto | 350 | 4 |
| `app-user-B1` | B/Home | 700 | 1 |
| `app-user-pairs` | A/Home, B/Auto | 1,150 | 3 |
| `app-user-none` | none | none | 0 |

## Report: `Synthetic.Report` (PBIR)

One table visual (Customer, Product, Total Amount, Activity Count), used for the Power BI Embedded
comparison in the portal. `definition.pbir` binds it to the model through the `{{SEMANTIC_MODEL_ID}}`
placeholder, which [scripts/Deploy-Report.ps1](../scripts/Deploy-Report.ps1) fills in.

Deploy both with the scripts in [scripts/](../scripts/).
