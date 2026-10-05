# Live validation — 2026-10-05

**Validation date:** 2026-10-05. **Scope:** this sample's approved synthetic semantic
model and fixed Customer/Product aggregate baseline, not a general RLS certification.

The rehearsal used a local Windows ASP.NET gateway on .NET 8, a real Fabric F8
capacity, Azure OpenAI GPT-5.4, and Microsoft Edge. Development authentication used
fictional Erin, Dan, and Frank with the same application-user key and `CUSTOMDATA()`
RLS role in the embedded-report and gateway query paths. No real customer data is
included.

**Code provenance:** baseline `4ad79bb2b61197a6b371e23257ad2bed21440a87`, plus the
production parser, chat, and parity implementation included in the upcoming commit.
This record does not assign a final implementation commit SHA.

## Implementation and recorded checks

| Area | Change or result |
|---|---|
| Production Arrow parser | Rejects multiple analytical result sets instead of silently selecting the first. |
| Chat result selection | Only `execute_dax` selects the analytical table; lookups cannot overwrite it. Failed analysis clears stale data; valid empty results retain their schema. |
| Frontend baseline comparison | Validates headers, records, and numeric values; rejects malformed or incomplete exports and distinguishes validated empty results from missing data. |
| Targeted backend regressions | 77 tests passed, exercising the production parser and chat selection. |
| Frontend parity/component regressions | 75 tests passed. |
| Typecheck and builds | Frontend typecheck/build and .NET solution build passed. |
| Full offline backend suite before publication | 143 tests passed. |
| Offline model-definition validator | 36 checks passed; no service calls or deployment. |
| Isolated frontend installation and full suite | `npm ci` with separate credential-free configuration succeeded; 77 tests and the production build passed. |

These are recorded rehearsal results, not a claim that the new GitHub Actions
workflow has already run.

## Same-grant live results

Fresh AI `execute_dax` calls and actual embedded-report baseline exports produced
the following matching Customer/Product totals and counts:

| Fictional persona | AI/gateway query result | Embedded-report baseline export |
|---|---|---|
| Erin | A / Home: 250, count 2; B / Auto: 900, count 1 | Same two rows; total 1,150, count 3 |
| Dan | B / Home: 700, count 1 | Same row; total 700, count 1 |
| Frank | Valid empty result with baseline schema | Valid empty export with baseline headers |

Direct engine probes using unfiltered `EVALUATE ALL('Scope')` returned only each
persona's granted scopes. As Dan, filtering `ALL('Scope')` to Customer A returned
no rows. These observations are distinct from the aggregate parity indicator.

Both metadata discovery and engine execution initially failed with
`CapacityNotActive`. They succeeded after an approved capacity resume, without
permission, model, or authentication changes.

## Public evidence

- [Structured persona proof](phase1-live-proof-2026-10-05.json) records identity,
  engine-scope, fresh-query, aggregate, and embedded-baseline outcomes.
- [Combined Erin report and AI capture](../images/embedded-ai-same-rls.png) shows
  the native report, baseline indicator, fresh AI answer, and result table.
- [Native report crop](../images/embedded-same-rls.png) shows the actual report
  headers and values. Those headers and values were also verified in the report
  DOM. The report iframe was inside the viewport when captured; both images were
  visually inspected. Neither PNG contains text/EXIF metadata or trailing data.

Only synthetic persona keys, report values, and application UI are published:
no credentials, tokens, tenant/workspace/dataset IDs, or resource identifiers.

## Evidence gaps / still thin

The aggregate check covers one fixed exported table, not every AI answer,
underlying row membership, synchronized report filters, or an RLS security
certification. Real OIDC, interactive OAuth, service-principal profiles, SSO,
customer models, and hosted throughput/concurrency are **not qualified** by this
rehearsal. Offline CI does not establish cloud authentication or engine-enforced RLS.

Fabric guidance reviewed: `microsoft/skills-for-fabric` at cached commit
`6c11ad58c25992e5d1435ce7cd80d217d5598a31`, using
`skills\semantic-model-authoring\SKILL.md`,
`skills\semantic-model-authoring\references\dax-guidelines.md`,
`common\COMMON-CORE.md`, `skills\powerbi-report-cli\SKILL.md`, and
`skills\powerbi-report-cli\references\authoring\screenshot-review.md`.
Refresh was blocked by unrelated checkout changes; the cache is not represented
as freshly synced. [Current Learn RLS guidance](https://learn.microsoft.com/en-us/power-bi/developer/embedded/cloud-rls)
was reviewed; Fabric blog retrieval returned HTTP 403. No broader product-status
claim is made.
