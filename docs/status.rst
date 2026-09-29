Implementation and evidence status
===================================

28 September 2026 - Stage 1 and demo integration
------------------------------------------------

Implemented:

* Reusable .NET query broker with fixed model/role routing and immutable
  request context; custom tool accepts DAX only.
* Certificate-only live token provider using a nonexportable RSA certificate
  in Windows CurrentUser/My. No ambient identity fallback.
* Strict Apache Arrow/LZ4 parser and deterministic 42-query RLS proof harness.
* Complete ten-part synthetic Import TMDL definition with ExternalAppScope
  and MetadataOnly roles.
* Reproducible model package, source references, fixture and documentation.
* React demo with six synthetic identities, five prepared questions, result
  table, LLM explanation and technical trace.
* .NET localhost API using the existing broker and Azure OpenAI Responses.
* Corrected scope: prepared question text, live IQ schema retrieval and
  LLM-generated DAX, rather than runtime prepared DAX templates.
* Streamed backend execution events with tool/API, credential-boundary, schema,
  generated-query and RLS-context visibility. Correlated console lifecycle logs.
* Persistent bounded live-call budget; disabled-by-default runtime.
* Presenter runbook and a local startup script.
* Customer overview v1: 14-slide PowerPoint with citations and presenter notes,
  plus three native-editable simplified security/comparison diagrams and PNGs
  under ``docs/customer-overview/``.

Observed locally:

* 83 xUnit tests passed, including the exact shared model fixture contract,
  request isolation, configuration and negative Arrow parsing.
* 31 offline model tests passed using the genuine TOM serializer, fixture
  arithmetic, complete definition parts and deterministic package hashing.
* These are NOT service-side DAX, M, RLS or Embedded execution results.
* Revised IQ demo: 165 backend tests cover planner transports/auth/schema/DAX,
  orchestration and streaming; 94 frontend tests cover real event framing,
  lifecycle/pagination, partial failures and stale-user isolation. Service
  responses in these tests are explicit test-only fixtures, not cloud evidence.

Current model package approval digest:

``71bd9df397ff25950e1ae087782c87e7ea5496e33eb1b89dc39b445fc269c7fc``

The model definition request digest is:

``a6a5a9b3aafff0bdfaa34e3ffb789c6aeaf4d4f131eacdcd8c2a035856b548ea``

Observed live in Stage 1:

* The synthetic Import model was deployed and processed.
* Certificate app-only queries with ``ExternalAppScope`` and ``customData``
  passed 42 live RLS acceptance checks.
* A1: 250 / 2 records; A2: 100 / 2; A3: 350 / 4; B1: 700 / 1;
  paired scopes: 1,150 / 3; no-access subject: 0 / 0 in the coalesced summary.
* ``ALL``, ``REMOVEFILTERS``, direct table projection and entitlement enumeration
  did not escape the assigned scope in the tested synthetic model.

The reviewed local evidence is ``stage1-evidence_v2.json``, recorded
2026-09-28T17:07:56Z against source commit
``ee6df8be48f2fa5cee6e8b15a2aba00f2646e5bd``. Raw service evidence and environment
identifiers remain outside the repository. The original fixture remains a
frozen expected-data artifact; its ``liveValidated:false`` is not runtime state
and does not supersede this separately recorded execution evidence.

Still pending:

* Exhaustive coverage of all 30 user/question combinations is not a goal of
  this educational sample. Selected live demonstrations are recorded below.
* Actual Power BI Embedded report/query parity.
* Detailed protocol-by-protocol architecture diagrams remain drafts. The new
  simplified customer diagrams cover the request-path security boundary,
  separate identities and proposed Embedded comparison; they do not claim
  the full conversational flow or Embedded parity has run.

Explicitly outside the narrowed proof:

* Free-text question entry, autonomous tool-selection orchestration and Foundry
  Agent Service. Prepared questions still require actual schema-based generation.
* Application login, revocation workflows, hosted web/MCP, load evaluation and
  production readiness. No public release has been authorized.

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

Integrated demonstration - 28 September 2026 (Eastern)
-------------------------------------------------------

The running localhost application completed real IQ metadata retrieval,
GPT-5.4 DAX generation, certificate-authenticated scoped execution and a separate
GPT-5.4 explanation. No cached schema, prepared DAX or canned answer was used.

Representative observed outcomes:

* A1 overview: amount 250, count 2; B1 overview: 700, count 1.
* A2 overview: 100, count 2; no-access overview: zero business records.
* Paired breakdown: A/Home 250 across 2 records and B/Auto 900 across 1.
* Paired subject asking for B/Home: no authorized rows.
* A1 details: actual activity keys 1 and 2, amounts 100 and 150.
* A3 daily comparison: January 1 amount 140/count 2; January 2 amount 210/count 2.
* B1 asking for B/Home: activity key 5, amount 700.

The schema projection hash in these runs was
``4d91316b227011ea6c04d98b62915052553f5756318f7aef668baaa8d72ac0e1``.
This identifies returned metadata, not a static schema substituted at runtime.
Safe case summaries are distinct from the operator-local full service evidence.

Three integration lessons are part of the example:

* IQ placed schema JSON in ``content[].text`` and only an artifact citation in
  ``structuredContent``. The adapter now distinguishes those shapes and checks
  the returned artifact identity.
* Hidden nonrelationship columns were absent from IQ metadata. The adapter
  projects only returned columns; it does not invent missing schema.
* Global example literals incorrectly encouraged a detail query to add B/Home
  filters. Literal hints are now question-specific, and the daily question
  explicitly asks for both amount and count. A successful RLS result is not
  proof that the LLM selected the correct business query.

Azure CLI also rejected passing tenant and subscription together. Inference
authentication now selects the subscription and independently validates the
returned tenant/principal claims.

The real React UI displayed the completed A1 execution timeline, explanation and
250/2 result after the stream parser was corrected to accept .NET's UTC
``+00:00`` timestamp representation as well as ``Z``. A transient IQ request
failure subsequently succeeded on a manual retry; no automatic or unscoped
fallback was added.

The demo selector is still not application login. The application does not
implement Power BI Embedded report rendering or prove report/chat parity.

`live-demo-observations_v1.json <live-demo-observations_v1.json>`_ contains the
reviewed synthetic summary without live tenant/resource identifiers.
