# Development and verification

Nothing here is needed to run the sample. These are the tools used while building it, kept apart so
the solution in `src/` stays easy to read.

| Folder | What it is | Run |
|---|---|---|
| [rls-harness/](rls-harness/) | A deterministic live RLS verifier. It sends the same unfiltered and adversarial DAX (direct scans, `ALL`, `REMOVEFILTERS`, denied scopes, entitlement enumeration) for every synthetic user and compares the engine's answers with [model/fixtures.json](../model/fixtures.json). It passed 42 of 42 checks live on 28 September 2026. | `dotnet test dev/rls-harness/IqRls.Tests` (offline). For a live run: `dotnet run --project dev/rls-harness/IqRls.Cli -- verify-rls --config <query-config.json> --allow-live` |
| [model-definition/](model-definition/) | Offline TMDL checks: parses the model with the Analysis Services TOM library, round-trips it, and checks tables, relationships, roles and fixtures. | `dotnet run --project dev/model-definition -- test .` |

The live harness reads a small JSON query configuration (tenant, workspace, model, service principal
client ID and certificate thumbprint, `identityMode`, role). Keep it outside the repository.

The existing test project also references the production gateway for offline Arrow-result and
analytical-table-selection regressions. These tests exercise the code in `src/`, not a duplicate
parser or selector. They do not call cloud services or establish engine-enforced RLS; the live
verifier and same-user report/chat rehearsal remain separate evidence.
