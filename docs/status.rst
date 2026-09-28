Implementation and evidence status
===================================

28 September 2026 - first local increment
----------------------------------------

Implemented:

* Reusable .NET query broker with fixed model/role routing and immutable
  request context; custom tool accepts DAX only.
* Certificate-only live token provider using a nonexportable RSA certificate
  in Windows CurrentUser/My. No ambient identity fallback.
* Strict Apache Arrow/LZ4 parser and deterministic 42-query RLS proof harness.
* Complete ten-part synthetic Import TMDL definition with ExternalAppScope
  and MetadataOnly roles.
* Reproducible model package, source references, fixture and documentation.

Observed locally:

* 83 xUnit tests passed, including the exact shared model fixture contract,
  request isolation, configuration and negative Arrow parsing.
* 31 offline model tests passed using the genuine TOM serializer, fixture
  arithmetic, complete definition parts and deterministic package hashing.
* These are NOT service-side DAX, M, RLS or Embedded execution results.

Current model package approval digest:

``71bd9df397ff25950e1ae087782c87e7ea5496e33eb1b89dc39b445fc269c7fc``

The model definition request digest is:

``a6a5a9b3aafff0bdfaa34e3ffb789c6aeaf4d4f131eacdcd8c2a035856b548ea``

Not yet completed:

* Deployment/Import processing and app-only role activation.
* Live engine-RLS results or Embedded parity.
* Live IQ metadata adapter, Foundry function-call loop and authenticated
  application-user resolver.
* Hosted web application, external MCP facade, load evaluation or public release.

Future live evidence
--------------------

For each approved run, retain locally: artifact/package hash, API and package
versions, principal mode, configuration scope, safe correlation IDs, expected
and actual row sets, outcomes, remaining gaps, cost lease and teardown result.

Only publish a reviewed synthetic summary. Do not commit live environment
configuration, tokens, application credentials, personal identity information,
approval records or raw service traces.

Passing the deterministic harness is a gate to further implementation, not a
claim of production support or arbitrary-model compatibility.
