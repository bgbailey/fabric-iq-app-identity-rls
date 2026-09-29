Application-owned identity for AI analytics
===========================================

Five minutes to understand the sample
-------------------------------------

**1. Start with the ISV's user, not a new Microsoft account.**
An ISV may already authenticate customers through its own portal. A customer
such as ``app-user-A1`` can be an opaque application subject; it need not be a
Microsoft Entra user. This demo substitutes a six-user selector for that login.
The substitution makes the idea visible, but it is not authentication.

**2. Separate the question from permission.**
"What is my total?" is a business question. "This subject may see A/Home" is an
authorization decision. The LLM writes DAX to answer the first. It cannot decide
the second, supply an identity, select a role or choose another model.

**3. Put the permission in the semantic engine.**
The trusted backend sends a fixed ``ExternalAppScope`` role and the subject in
``customData``. Inside the Import model, ``CUSTOMDATA()`` finds that subject's
exact allowed scope keys. RLS restricts ``Scope`` and propagates to ``Activity``.
This protects an unfiltered query too; it does not depend on remembering a
customer predicate in every generated query.

**4. Give the LLM only what each step needs.**
Fabric IQ MCP supplies live metadata. A reviewed structural projection, not
entitlements or arbitrary model instructions, goes to DAX generation.
After execution, a fresh LLM call sees only the question and authorized rows.
IQ's delegated user, the query service principal and the LLM caller are service
identities, not the selected application subject.

**5. Inspect the query and rows, not just the answer.**
The portal streams real operation events and shows generated DAX, row data and
the explanation. A fluent answer is not proof of either RLS or query correctness.
The exact rows and question intent matter.

Try the `quickstart <quickstart.rst>`_, or read the
`ownership table and sequence <architecture.rst>`_.

The tiny dataset makes mistakes visible
---------------------------------------

The fixture has six activity records, total 1,950, in four customer/product
pairs. No customer, personal or production data is used.

.. list-table:: Expected unfiltered fixture results, not a completed UI matrix
   :header-rows: 1
   :widths: 24 30 15 13 18

   * - Application subject
     - Allowed pairs
     - Total
     - Activity count
     - Activity keys
   * - ``app-user-A1``
     - A/Home
     - 250
     - 2
     - 1, 2
   * - ``app-user-A2``
     - A/Auto
     - 100
     - 2
     - 3, 4
   * - ``app-user-A3``
     - A/Home; A/Auto
     - 350
     - 4
     - 1, 2, 3, 4
   * - ``app-user-B1``
     - B/Home
     - 700
     - 1
     - 5
   * - ``app-user-pairs``
     - A/Home; B/Auto
     - 1,150
     - 3
     - 1, 2, 6
   * - ``app-user-none``
     - None
     - BLANK or coalesced 0
     - BLANK or coalesced 0
     - None

The pair case is the important one: two customer names and two product names
must not accidentally become four permitted combinations. Entitlements use
scope keys, not a Cartesian product of independent lists.

A summary query can return **one result row with a count of zero**. Result-row
count is not activity count. The model's raw empty measures are BLANK;
``COALESCE`` in a query can render zero. A neutral Date row is not evidence that
the subject has activity on that date.

Five prepared questions, not five prepared queries
--------------------------------------------------

``DemoCatalog`` provides these question IDs:

* ``overview``: total and activity count.
* ``breakdown``: group activity by customer and product.
* ``daily``: compare January 1 and January 2 in the synthetic January 2026 data.
* ``details``: show activity records behind the total.
* ``customer-b-home``: request B/Home, even when the subject cannot access it.

Every live run retrieves IQ metadata and generates DAX again. Native IQ
``ExecuteQuery`` and ``ValueSearch`` are not used. A failure is visible; there
is no stored answer, static schema or DAX-template fallback.

Useful walkthrough: run overview for A1 and B1, then the paired subject's
breakdown, then ask the paired subject for B/Home. The first two totals differ;
the breakdown contains only A/Home and B/Auto; the B/Home request should return
no authorized data. These selected cases completed live on 28 September 2026.
The latest evidence boundary is in `status <status.rst>`_.

Two questions to ask about every result
---------------------------------------

**Authorization correctness: could this subject see a forbidden row?**
The deterministic CLI sends the same unfiltered DAX for different subjects,
including fact projections, entitlement enumeration and attempted
``ALL``/``REMOVEFILTERS`` bypasses. Its 42 live checks passed independently of
the LLM. Do not weaken RLS to make an answer look right.

**Business-query correctness: did the DAX answer this question?**
Generative runs may produce different DAX. An irrelevant B/Home filter in an
A1 detail query can correctly return nothing under RLS yet still be the wrong
answer to the question. Inspect filters, measures, granularity and dates.
Schema-grounded generation and a syntactically accepted query do not prove
semantic correctness; irrelevant prompt hints can misdirect a query.

Similarly, explanation quality is a third concern: check that every figure
comes from current rows. "No authorized data returned for this question" does
not mean there is no data elsewhere.

Follow one request through the code
-----------------------------------

1. `App.tsx <../src/DemoWeb/src/App.tsx>`_ and
   `api.ts <../src/DemoWeb/src/api.ts>`_: user/question selection, NDJSON events
   and result rendering. ``RequestGate`` discards late responses after a switch.
2. `Program.cs <../src/IqRls.Demo/Program.cs>`_: loopback-only host,
   ``GET /api/demo``, ``POST /api/ask`` and streamed response frames.
3. `DemoRunner <../src/IqRls.Demo/DemoRunner.cs>`_ and
   `ServerContext <../src/IqRls.Core/ServerContext.cs>`_: resolve the immutable
   subject, run planning/execution/explanation, and keep identity out of DAX input.
4. `LiveDemoPlanner <../src/IqRls.Demo/LiveDemoPlanner.cs>`_ and
   `FabricIqSchemaClient <../src/IqRls.Demo/FabricIqSchemaClient.cs>`_: delegated
   authentication, MCP initialization, tool discovery and schema retrieval.
5. `ReviewedSyntheticSchema <../src/IqRls.Demo/ReviewedSyntheticSchema.cs>`_:
   project actual returned metadata. Hidden columns omitted by IQ are not
   invented; citation metadata is not mistaken for a duplicate schema.
6. `ResponsesDaxGenerator / GeneratedDaxPolicy
   <../src/IqRls.Demo/ResponsesDaxGenerator.cs>`_: a fresh structured-output
   generation request and deliberately narrow query checks, not authorization.
7. `QueryBroker <../src/IqRls.Core/QueryBroker.cs>`_ and
   `CertificateTokenSource <../src/IqRls.Core/CertificateTokenSource.cs>`_: add
   the fixed role and trusted subject to a certificate-authenticated REST call.
8. `ExternalAppScope role
   <../model/Synthetic.SemanticModel/definition/roles/ExternalAppScope.tmdl>`_
   and `relationships
   <../model/Synthetic.SemanticModel/definition/relationships.tmdl>`_:
   ``CUSTOMDATA`` -> exact entitlement -> Scope -> Activity.
9. `ArrowResponseParser <../src/IqRls.Core/ArrowResponseParser.cs>`_ and
   `ResponsesExplainer <../src/IqRls.Demo/CloudClients.cs>`_: reject error streams,
   preserve values and explain authorized results without conversation history.
10. `LiveBudget <../src/IqRls.Demo/LiveBudget.cs>`_ and
    `ExecutionSession <../src/IqRls.Demo/ExecutionSession.cs>`_: persist call
    attempts and make actual execution observable.

What an ISV would adapt next
----------------------------

Replace fixture selection with your application's authenticated subject
resolution, and decide how entitlements are maintained and revoked. Keep roles,
credentials and model routing server-controlled. Review metadata exposure for
your own model; this projector intentionally recognizes only the synthetic one.

An existing Embedded implementation can reuse its application identity and
entitlement concepts, but identity transport is different. Read the
`Embedded comparison <power-bi-embedded-comparison.rst>`_ before assuming that a
``USERNAME()`` role or an embed token works unchanged with query REST.
These are adaptation directions, not implemented production features.
