Fabric IQ application-identity RLS
=================================

An experimental reference implementation of application-owned authorization
over a Power BI semantic model. The intended agent architecture keeps reviewed
Fabric IQ MCP metadata, replaces native query execution with a trusted custom
tool, and makes the semantic model enforce row-level security.

Current status
--------------

The first local synthetic model and broker increment is implemented. Cloud
deployment, live RLS, Embedded parity and the IQ/Foundry integration have NOT
been demonstrated. Do not treat offline tests as evidence of engine RLS.
This is not a supported-product announcement or a production-ready sample.

The first gate is deliberately independent of an LLM: send the SAME unfiltered
DAX for several application subjects and demonstrate different authorized rows
through ``executeDaxQueries`` with ``roles`` and ``customData``.

Architecture
------------

::

    Application authentication
      -> trusted subject / current entitlement decision
      -> reviewed semantic metadata -> Foundry model
      -> custom execution function (DAX only)
      -> guarded Power BI query broker
      -> executeDaxQueries + model role + CUSTOMDATA
      -> semantic-model RLS -> bounded typed result

    Same authorization decision
      -> GenerateToken effective identity
      -> embedded report using the same semantic model

The LLM must never supply credentials, subject identity, security roles,
customer/product grants or physical model/workspace IDs.

What is in the first increment
-----------------------------

* ``model/``: deterministic Import-mode semantic model, customer/product-pair
  entitlements and expected row-level results.
* ``tools/model-definition/``: offline TOM validation and content-addressed
  Fabric definition packaging.
* ``src/``: request-scoped query broker and developer-only proof CLI.
* ``tests/``: local request-policy, parsing and fixture tests.
* ``docs/``: architecture decisions, staged deployment and evidence criteria.

The developer CLI's subject selector is a TEST HARNESS. It is not application
authentication and must not be exposed as a public endpoint.

Why these choices
-----------------

The newer Power BI ``executeDaxQueries`` operation documents ``customData``,
named ``roles``, and role selection by a workspace-admin service principal.
It returns Apache Arrow IPC, including error rowsets on HTTP 200. The older
JSON ``executeQueries`` API is not an app-only RLS fallback.

GA Fabric IQ MCP uses delegated Entra work/school authentication, not app-only
authentication. The calling agent composes DAX. Native ``ExecuteQuery`` AND
``ValueSearch`` must be excluded from the application's metadata path.

Reusing Embedded means reusing the entitlement resolver, role and model.
An embed token is not a replacement bearer token for the query REST API.

See ``docs/architecture.rst`` for the limits and ``model/README.rst`` for the
fixture. Source-specific references are in ``model/references.json`` and the
architecture document.

Local preparation
-----------------

Requires a .NET SDK that supports net8.0. No cloud credentials are needed to
validate or package the model::

    dotnet restore .\tools\model-definition\ModelDefinition.csproj --locked-mode
    dotnet run --project .\tools\model-definition\ModelDefinition.csproj --no-restore -- test
    dotnet run --project .\tools\model-definition\ModelDefinition.csproj --no-restore -- package

Broker build/test and live CLI usage are documented with that component.
Pin and restore its package lock files before running it::

    dotnet restore .\IqRls.sln --locked-mode
    dotnet test .\tests\IqRls.Tests\IqRls.Tests.csproj --no-restore
    dotnet run --project .\src\IqRls.Cli --no-build -- selftest

See ``docs/status.rst`` for the evidence boundary and current implementation
status; see ``src/IqRls.Cli/README.rst`` for the live command contract.

Safe deployment and publication
------------------------------

No command in this README grants permission to deploy. An operator must
explicitly approve the actual target, compiled artifacts, role assignments,
credential lifetime, capacity lease, cost estimate/cap and teardown.
See ``docs/deployment.rst``.

Keep deployment configuration, credentials, certificates/private keys,
approval attestations, token caches and raw live evidence OUTSIDE this
repository. Only synthetic fixtures and reviewed/redacted results belong here.
The repository starts private; public release is a separate approval gate.

No license is granted by a private development draft. Select and add the
appropriate project license and preserve dependency/sample notices before
public distribution.
