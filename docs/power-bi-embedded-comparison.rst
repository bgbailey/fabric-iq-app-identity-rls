Power BI Embedded and this AI query path
========================================

Baseline: **28 September 2026**. This is an architectural comparison, not a
report-parity result. The repository does not create an embedded report, call
``GenerateToken`` or render a Power BI report frame.

What carries over from an ISV's Embedded application
----------------------------------------------------

In an app-owns-data design, the ISV authenticates its own users and the backend
decides their data access. Those users need not each have a Power BI/Entra
account. Both an embedded report and an AI answer can use that same application
subject and entitlement decision, with model-engine RLS enforcing allowed rows.

The sample's selector is only a teaching substitute for that application
authentication. It does not implement an existing portal's login integration.
See `Embedded RLS
<https://learn.microsoft.com/en-us/power-bi/developer/embedded/embedded-row-level-security>`_
and `embed tokens
<https://learn.microsoft.com/en-us/power-bi/developer/embedded/embed-tokens>`_.

The differences that matter
---------------------------

.. list-table::
   :header-rows: 1
   :widths: 23 38 39

   * - Concern
     - Power BI Embedded, app owns data
     - This sample
   * - User experience
     - Report visuals embedded in the application.
     - Prepared question, generated DAX, exact result table and LLM explanation.
   * - Backend authorization transport
     - ``GenerateToken`` with applicable ``EffectiveIdentity`` fields,
       including datasets, roles, username and supported customData.
     - Per-query ``executeDaxQueries`` body with fixed ``roles`` and
       application subject as ``customData``.
   * - Browser token
     - Embed token authorizes the supported embedded content/session.
     - No Power BI token reaches the browser; only the local API's data/events.
   * - Query API authentication
     - An embed token is not a general REST credential.
     - Entra app-only bearer token acquired with a certificate.
   * - Identity function in RLS
     - Existing designs often use ``USERNAME()`` with effective ``username``;
       customData depends on the documented connection/model scenario.
     - This Import model explicitly uses ``CUSTOMDATA()`` and exact subject
       matching. No ``effectiveUsername`` is sent.
   * - Schema/planning
     - Visual definitions determine report queries and filters.
     - IQ delegated metadata plus an LLM determine the business query.
   * - Result format
     - Report visuals and supported exports.
     - Arrow IPC/LZ4 decoded by the backend; rows then go to explanation.
   * - Evidence here
     - No live Embedded report/token/row-parity run.
     - 42 engine-RLS checks and selected integrated live AI-query cases.

The token and identity fields are documented in `GenerateToken
<https://learn.microsoft.com/en-us/rest/api/power-bi/embed-token/generate-token>`_;
the actual query body is documented in `Execute DAX Queries In Group
<https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/execute-dax-queries-in-group>`_.
They are different APIs, not interchangeable SDK calls.

USERNAME is not CUSTOMDATA
--------------------------

Supplying ``customData`` does not populate ``USERNAME()`` with an arbitrary
application subject. Supplying an Embedded ``username`` does not make it the
same property as ``CUSTOMDATA()``. An existing username-based role therefore
does not automatically work unchanged with this broker.

For a real integration, explicitly align the role expression and the identity
property transported by **each supported path**. Reuse the entitlement model
where possible, but verify the selected model/connection type's custom-data
support rather than assuming identical behavior. See
`cloud RLS and effective identity
<https://learn.microsoft.com/en-us/power-bi/developer/embedded/cloud-rls>`_ and
`CUSTOMDATA <https://learn.microsoft.com/en-us/dax/customdata-function-dax>`_.

``identityBlob`` and source SSO address different identity requirements; neither
is a generic container for the app's entitlement key. Do not send an embed token
to ``executeDaxQueries``, ordinary REST endpoints or XMLA as a substitute for
their required authentication.

An important broker responsibility
----------------------------------

For this query API, a service principal selecting roles requires workspace
Admin. It must still send ``ExternalAppScope`` on **every** request. A call with
the same privileged credential but no selected role may be unrestricted.
The broker, not just the model definition, is part of the trust boundary.

Both UI paths must derive authorization on the server, never from browser
role lists or LLM output. The AI path also has a separate IQ delegated user:
its endpoint-wide scopes and metadata tool allowlist do not create an
app-subject-aware Embedded session.

What would establish actual parity?
-----------------------------------

As a separate follow-on exercise:

1. Use a real report on the same synthetic model and align its RLS identity
   contract with the REST query path.
2. Issue a real embed token from the same trusted application-subject decision.
3. Match report/page/visual filters, measure definitions, grain and date scope
   to the comparison DAX; visual filters are not themselves RLS.
4. Compare exact authorized scope/activity rows and totals, especially paired
   A/Home + B/Auto access and denied B/Home, not just screenshots or payloads.

``visual.exportData`` exports a supported existing visual's data; it is not
arbitrary DAX execution and its limitations matter when comparing results.
Identical-looking token payloads do not prove identical engine behavior.
**None of this report-parity exercise has been completed by this sample.**

Start with the `working architecture <architecture.rst>`_ rather than treating
the comparison as a feature checklist already implemented.
