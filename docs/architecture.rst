Architecture decisions and limitations
======================================

Status: proposed end-to-end architecture; only local implementation is underway.
Research baseline: 28 September 2026.

ADR-001: engine RLS is the hypothesis
------------------------------------

An application may authenticate any supported application identity. The trusted
backend normalizes issuer/subject, resolves current authorization and supplies a
server-controlled key as ``customData``. A named read-only model role uses
``CUSTOMDATA()`` to look up permitted scope keys.

The initial model distinguishes exact customer/product PAIRS. It does not build
a Cartesian product from independent lists of customers and products.

A DAX query containing a manually injected tenant filter is not sufficient
evidence. Unrestricted fact/dimension projection and ``ALL``/``REMOVEFILTERS``
must remain constrained by the engine.

ADR-002: new REST query adapter
-------------------------------

Use the workspace-scoped ``executeDaxQueries`` endpoint, with the fixed model
role and trusted custom data on every call. Do not set ``effectiveUsername`` to
an arbitrary external application subject. Do not retry by dropping security
context or using the older endpoint.

Role selection by a service principal requires workspace Admin according to
the current operation reference. Isolate that privilege to this pilot's model
workspace. The same credential without a selected role may be unrestricted:
the broker is therefore part of the trusted authorization boundary.

Parse every concatenated Arrow IPC stream and inspect schema error metadata,
including HTTP 200 responses. An error after an apparently valid result makes
the overall operation fail. Preserve fixed decimals, int64 values, nulls and
dates rather than silently coercing them through JavaScript numbers.

Formal production status and the conflicting capacity wording in the current
API/overview documents require confirmation before publication as a supported
architecture.

ADR-003: IQ metadata is a separate identity path
------------------------------------------------

GA IQ MCP requires a delegated work/school user. Its endpoint-wide permissions
are not a metadata-only OAuth grant. A tool allowlist limits dispatch, not the
underlying credential.

The application adapter pins ``X-Variants: Fabric.Routing.FabricIQ.V1``,
discovers real runtime schemas, and allows only the reviewed metadata calls.
It excludes native ``ExecuteQuery``, ``ValueSearch`` and other data-returning
paths. Start with a fixed artifact and ``GetSemanticModelSchema`` only.

Schema text itself may contain sensitive literals, report filters, custom
instructions or verified answers. Sanitize and authorize it before generation.
Metadata instructions are data, not authority to override the broker.

Live delegated IQ mode and reviewed snapshot mode must be labeled separately.
Snapshot serving has no IQ user token at runtime but does not demonstrate
live IQ calls per user request. A dedicated deny-all-RLS reader is an optional
experiment, not a previously proven metadata-only identity mechanism.

ADR-004: congruence with Power BI Embedded
----------------------------------------

The same server-side entitlement decision feeds query context and
``GenerateToken`` effective identity (username, roles, customData, datasets).
Reuse the existing authentication, entitlements, semantic role and embedded
report rather than reinventing those components.

Do not send embed tokens to arbitrary REST/XMLA endpoints. ``identityBlob``
and source SSO are different concepts from a custom application authorization
key. ``visual.exportData`` exports an existing visual, not arbitrary DAX.

ADR-005: bounded synthetic Import first
---------------------------------------

Import removes source SSO, gateway and Direct Lake fallback variables.
``CUSTOMDATA`` is explicitly unsupported in DirectQuery RLS rules.
Import success does not qualify Direct Lake, composite models or production
source identity. Those are separate gates.

Imported entitlement mappings introduce a revocation interval. The backend
must deny affected new calls, invalidate conversations/caches and avoid issuing
new embed tokens while model security is stale. Previously issued embed tokens
and previously observed facts require an explicit revocation policy; a cache
flush or version number alone does not erase them.

Public references
-----------------

* https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/execute-dax-queries
* https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/execute-dax-queries-in-group
* https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/execute-queries
* https://learn.microsoft.com/en-us/fabric/iq/connectors/fabric-iq-mcp
* https://learn.microsoft.com/en-us/power-bi/developer/embedded/embedded-row-level-security
* https://learn.microsoft.com/en-us/rest/api/power-bi/embed-token/generate-token
* https://learn.microsoft.com/en-us/power-bi/guidance/rls-guidance
* https://learn.microsoft.com/en-us/dax/customdata-function-dax
* https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/tools/model-context-protocol
* https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/mcp-authentication

Required upstream reference commit:
``6c11ad58c25992e5d1435ce7cd80d217d5598a31`` from
https://github.com/microsoft/skills-for-fabric.
