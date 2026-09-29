Architecture: the implementation, not a future platform
=======================================================

Implementation and documentation baseline: **28 September 2026**. Selected
integrated live runs succeeded; see `status <status.rst>`_ for exact evidence.
Start with the `learning guide <learning-guide.rst>`_ for a shorter explanation.

The app controls authorization; the LLM proposes a query
--------------------------------------------------------

The localhost React portal sends only a catalog ``userId`` and ``questionId``.
``DemoRunner`` creates an immutable ``ServerContext`` from ``SyntheticSubjects``.
This is fixture selection, not authentication. In a real ISV application, an
authenticated application session would supply the subject instead.

The planner receives the question, not this subject's grants. Fabric IQ supplies
live semantic metadata; a GPT-5.4 Responses call generates DAX. ``QueryBroker``
then supplies ``roles:["ExternalAppScope"]`` and the subject as ``customData``.
The semantic engine evaluates the role before returning rows. A separate
stateless Responses call explains only the current question and returned rows.

``execute_dax`` is the name used for our internal executor boundary. It is **not**
a replacement registered inside Microsoft's MCP service, and this application
does not host an external MCP server. The orchestrator deliberately does not
dispatch IQ's native ``ExecuteQuery`` or ``ValueSearch``.

Four identities, four jobs
--------------------------

.. list-table::
   :header-rows: 1
   :widths: 18 36 46

   * - Identity
     - Authentication
     - Responsibility
   * - Application subject
     - None in this demo: synthetic selector
     - Opaque, case-sensitive app-owned key. Need not exist in Entra.
   * - IQ metadata user
     - Delegated Entra work/school sign-in through a public client
     - Read the fixed model's schema through Fabric IQ MCP.
   * - Query service principal
     - Entra certificate client credentials
     - Call Power BI REST with the fixed role and application ``customData``.
   * - LLM caller
     - Azure CLI delegated credential for Azure OpenAI
     - Authenticate generation and explanation, not semantic-model access.

The IQ and LLM credentials are separate paths. This implementation requires them
to use the same configured development user and requires all three cloud paths
to use the same tenant; it does not implement independent arbitrary-tenant
routing. IQ's public-client ID is not the query service principal's client ID.

IQ requests delegated ``Item.Read.All``, ``Item.Execute.All`` and
``Dataset.Read.All`` against the Power BI audience. These are **endpoint-wide
permissions, not a metadata-only OAuth grant**. The tool allowlist constrains
this application's dispatch; it does not reduce the token's privileges.
See the `Fabric IQ MCP authentication and tool reference
<https://learn.microsoft.com/en-us/fabric/iq/connectors/fabric-iq-mcp>`_.

Trust boundaries and parameter ownership
----------------------------------------

.. list-table::
   :header-rows: 1
   :widths: 25 30 45

   * - Value or decision
     - Owner in this sample
     - What crosses the boundary
   * - Selected subject / question ID
     - Local browser, validated against the catalog
     - Only these two IDs enter ``POST /api/ask``. Not an authenticated claim.
   * - Question text
     - ``DemoCatalog``
     - Prepared text goes to generation and explanation; no prepared DAX.
   * - Immutable application subject
     - ``SyntheticSubjects`` / ``ServerContext``
     - Broker uses it as ``customData``; generator does not select it.
   * - Customer/product grants
     - Model's ``User Access`` table
     - Role evaluates exact scope keys. Grants are not generator input.
   * - Model/workspace IDs and service endpoints
     - Validated operator configuration and fixed endpoint code
     - Never accepted from the browser or generated query.
   * - Model role
     - Fixed broker contract: ``ExternalAppScope``
     - Attached to every REST query, not chosen by the LLM.
   * - IQ schema and model-authored text
     - Untrusted service response, projected by ``ReviewedSyntheticSchema``
     - Only recognized structural metadata reaches generation.
   * - DAX and business filters
     - LLM proposal, checked by ``GeneratedDaxPolicy``
     - One bounded query reaches the broker. Validation is not RLS.
   * - Authorized result
     - Semantic engine, then ``ArrowResponseParser``
     - Typed, lossless values go to the UI and explanation call.
   * - Natural-language answer
     - LLM output
     - Untrusted prose, displayed beside exact rows; not an access decision.
   * - Credentials
     - Backend credential providers
     - Bearer headers go only to the corresponding service, never to prompts/UI.

**The broker is trusted.** The current
`executeDaxQueries operation
<https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/execute-dax-queries-in-group>`_
requires workspace Admin for a service principal selecting roles. That elevated
credential may access unrestricted data if the role is omitted. Isolate it to
this synthetic workspace. Never retry a failed request without ``roles`` or
``customData`` and never give these fields to the LLM to fill in.

Actual request sequence
-----------------------

::

    Browser -> .NET: POST /api/ask {userId, questionId}
        .NET: resolve immutable application subject
        .NET -> IQ MCP: initialize
        .NET -> IQ MCP: notifications/initialized
        .NET -> IQ MCP: tools/list
        .NET -> IQ MCP: tools/call GetSemanticModelSchema {artifactId}
        IQ MCP -> .NET: schema text + artifact citation
        .NET: validate and project returned metadata
        .NET -> OpenAI Responses: question + schema
        OpenAI -> .NET: generated DAX
        .NET: validate DAX; broker adds fixed role + trusted customData
        .NET -> Power BI executeDaxQueries: secured query request
        Power BI -> .NET: RLS-filtered Arrow/LZ4 result
        .NET: parse ALL streams; reject error rowsets
        .NET -> OpenAI Responses: question + current authorized rows
        OpenAI -> .NET: explanation
    .NET -> Browser: result frame with answer, exact rows and trace

    .NET -> Browser: NDJSON event frames also arrive throughout execution.

All four MCP messages go to
``https://fabriciq.svc.cloud.microsoft/v1/mcp/fabriciq`` with
``X-Variants: Fabric.Routing.FabricIQ.V1``. The adapter propagates negotiated
protocol/session headers and discovers the actual tool input schema.
``GetSemanticModelSchema`` receives the configured semantic model's
``artifactId``; no report discovery is needed for this known model.

The broker's wire request is::

    POST https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}/datasets/{datasetId}/executeDaxQueries
    Accept: application/vnd.apache.arrow.stream
    Authorization: Bearer <query-service-principal-token>

    {
      "query": "<generated-DAX>",
      "roles": ["ExternalAppScope"],
      "customData": "<server-resolved-application-subject>",
      "queryTimeout": 30,
      "resultSetRowCountLimit": 1000
    }

Both LLM calls use ``POST /openai/v1/responses`` on the configured Azure OpenAI
resource. Generation uses structured JSON output containing only ``query``.
Explanation has no tools, conversation ID or previous-response ID. Both use
``store:false``; this is not a blanket statement about service retention.
See the `Responses API guide
<https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/responses>`_.

What "live schema" means here
-----------------------------

The observed IQ response put JSON schema text in ``content[0].text`` and
``artifact_citation`` in ``structuredContent``. They are **not two copies of the
schema**. The adapter checks available artifact IDs and projects recognized
tables, returned columns, measure names and relationships.

``User Access``, expressions, descriptions, literal examples, custom instructions
and verified answers are excluded from generation. Hidden nonrelationship
``Activity Key`` and ``Amount`` columns were absent from the live IQ metadata;
the adapter does not fabricate them. Relationships may use
``UnidirectionalFilter`` text. An unrecognized, partial or truncated response
fails instead of falling back to a static schema.

This is a model-specific teaching adapter, not a general semantic-model
sanitizer. The generator may use reviewed synthetic business literals, but those
are not live value-search results or grants. Irrelevant literal hints can cause
an incorrect business filter even when RLS is working.

The normal upstream IQ workflow includes value search, native query execution,
custom instructions and verified answers. Excluding those is a deliberate
deviation for this application-identity experiment, not a claim that IQ
natively supplies application-subject RLS.

How the model enforces exact pairs
----------------------------------

The four inline Import tables are ``Scope``, ``Activity``, ``Date`` and
``User Access``. ``ExternalAppScope`` filters ``Scope`` by matching
``CUSTOMDATA()`` to an entitlement subject with case-sensitive ``EXACT`` and
matching a single ``Scope Key``. It also filters ``User Access`` itself.

``User Access`` is disconnected, not a bidirectional relationship bridge.
One-direction security propagation from ``Scope`` to ``Activity`` restricts
fact rows. ``Date`` independently filters Activity and remains a neutral,
shared January 2026 calendar. Hidden objects are not a security control.

Granting A/Home and B/Auto does **not** grant A/Auto or B/Home. ``ALL`` and
``REMOVEFILTERS`` must not expand the role's visible rows. The deterministic
CLI established this separately from LLM generation. The optional
``MetadataOnly`` role in the package is not proof of a metadata-only IQ identity.

See `CUSTOMDATA
<https://learn.microsoft.com/en-us/dax/customdata-function-dax>`_ and
`RLS guidance <https://learn.microsoft.com/en-us/power-bi/guidance/rls-guidance>`_.
Import avoids source-SSO and gateway variables; its result does not qualify
DirectQuery, Direct Lake or composite models. ``CUSTOMDATA`` is documented as
unsupported in DirectQuery RLS rules.

Result handling, visibility and limits
--------------------------------------

The Arrow parser examines every concatenated IPC stream, including error
metadata on HTTP 200. Errors after valid-looking data still fail the request.
It decodes LZ4, preserves decimal/int64 values as invariant strings and preserves
nulls. It rejects unsupported shapes and results hitting the row cap rather
than calling them complete. The older JSON ``executeQueries`` API is not a
fallback for this app-only RLS path.

NDJSON carries real backend events followed by a final ``result`` or ``error``.
HTTP 200 alone is not success. The inspector shows sanitized calls, schema,
generated DAX, role/customData, timing and model usage. Console logs contain
correlated lifecycle fields, not payloads or credentials. Switching subjects or
questions clears old output and discards late events. Answers are not cached.

The host binds to ``127.0.0.1:5187`` and rejects alternate hosts/origins and extra
request fields. Live operation needs external configuration plus ``--allow-live``.
The persistent configuration-bound ledger allows at most 100 service-call
attempts, two-second spacing and one in-flight request. A usual question uses
seven calls; extra tool-list pages add calls. Failed attempts remain spent.
The total request deadline is four minutes, bounded further by the live expiry.
These are demo controls, not a cloud spending cap or a production security claim.

The LLM credential sets ``AzureCliCredentialOptions.Subscription`` only:
Azure CLI rejects simultaneous ``--tenant`` and ``--subscription`` token
arguments. Returned tenant, expiry and principal claims are checked before
transmitting data; Azure verifies the token itself. IQ separately checks its
delegated client, audience, scopes and principal. No fallback credential chain
is used. See `AzureCliCredentialOptions
<https://learn.microsoft.com/en-us/dotnet/api/azure.identity.azureclicredentialoptions>`_.

What remains outside this sample
--------------------------------

Real application authentication, entitlement updates/revocation, hosted
credentials, external MCP hosting and actual Embedded report parity are not
implemented. An imported entitlement table also implies refresh latency; a
production revocation design must handle that deliberately.

Fabric IQ MCP's documented GA status is not a support guarantee for this
composition. Confirm current executeDaxQueries availability, capacity requirements
and tenant settings for your environment; the operation and overview references
have had differing capacity wording. This sample records observed behavior,
not universal SKU support.

Reference provenance
--------------------

The project's 28 September 2026 research baseline uses
`microsoft/skills-for-fabric
<https://github.com/microsoft/skills-for-fabric/tree/6c11ad58c25992e5d1435ce7cd80d217d5598a31>`_,
commit ``6c11ad58c25992e5d1435ce7cd80d217d5598a31``:
``skills/fabriciq/SKILL.md``, ``skills/semantic-model-authoring/SKILL.md``,
its ``references/semantic-model-rest-api.md`` and ``references/dax-guidelines.md``,
and ``common/COMMON-CORE.md``, ``common/COMMON-CLI.md`` and
``common/ITEM-DEFINITIONS-CORE.md``. Original model references are in
`model/references.json <../model/references.json>`_.
The docs-only pass read that cached commit without making new network calls.
Use current Learn and `Fabric announcements <https://blog.fabric.microsoft.com/>`_
when checking availability for a new deployment. Running this sample does not
require installing those skills or Scout.
