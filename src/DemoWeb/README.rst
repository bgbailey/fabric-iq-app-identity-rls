Scoped Analytics web demo
========================

Local React/TypeScript technical proof, not a production application. All people,
scopes, question text, results and explanations come from the local backend. The
user selector is a demo identity selector, not authentication. There is no
Power BI report frame in this increment; the table is "Semantic model results".

Build and verify
---------------

Requires Node.js 24 or later and npm. From this directory::

    npm ci
    npm test
    npm run build

The compiled bundle is ``src\DemoWeb\dist\index.html`` with sibling ``assets``.
The parent demo backend serves this directory at ``http://127.0.0.1:5187``.
Use the repository's backend startup instructions. Do not open the HTML as a
standalone file, and do not run it on a separate Vite origin. This project does
not start a server or enable live service access during build or tests.

The host must serve the generated JS/CSS assets as well as the index. ``dist``
and ``node_modules`` are generated directories and should remain git-ignored.
Rebuild after source changes. There are no runtime fonts, images or scripts
loaded from external hosts.

API boundary
------------

The authoritative contract is ``docs\demo-api.json`` at the repository root.

* ``GET /api/demo`` loads the identities, scopes, prepared questions, approved
  live-window state, configured model and evidence note. Its execution mode must
  be ``fabric-iq-generated-dax``.
* ``POST /api/ask`` sends JSON containing only ``userId`` and ``questionId``.
  Required headers are ``X-Demo-Request: 1`` and
  ``Accept: application/x-ndjson``. Role, CustomData, DAX and result rows are
  never sent by the browser. Fetch uses no credentials.
* The answer must have ``answerSource: "llm"`` and identify the requested user
  and question. The result's row count must match the returned rows.
* Cells remain strings or nulls. Decimal values never pass through JavaScript
  Number conversion; null is shown as ``NULL``, distinct from an empty string.
* A missing model, disabled live access or missing/expired window disables Run.
  Configuration refresh is explicit; it never runs a semantic-model query.
* Controlled server errors show the exact message, error code, HTTP status and
  correlation ID where supplied. There are no automatic retries, cached answers,
  offline fixtures, client-side RLS filters or fabricated LLM responses.
* Successful HTTP responses must contain an NDJSON event/result/error stream.
  UTF-8 is decoded incrementally with fatal validation. Limits are 1 MiB total,
  256 KiB per line and 256 events. Unknown or malformed frames, mismatched request
  IDs, nonincreasing sequence numbers, invalid stage transitions, missing
  completion, unexpected EOF and any frame after a terminal frame fail closed.
  The result is not exposed until the stream ends. Errors after HTTP 200 remain
  errors. Pre-stream non-2xx responses may use ordinary JSON.
  A ``request``-step failure/cancellation is displayed separately without
  changing already-completed stage statuses.
  Stages can contain multiple real operations: a completed operation can be
  followed by a new started/completed pair, without imposing a fixed cardinality.
  ``iq-initialize`` and ``iq-tools`` currently use this for lifecycle/paging.
  These stages show operation completion as unconfirmed stage completion until
  the backend's overall completion event; initialization notification and
  pagination are not silently skipped or treated as already complete.

Changing user or question immediately aborts and clears all prior work. A
sequence gate also discards late events and results from transports which ignore
abort. Refreshing the catalog clears everything too. The selected question
survives user changes, enabling a deliberate same-question comparison, not a
guarantee of identical DAX. Failure and cancellation retain received diagnostics
only for the current selection. Cancel does not promise rollback: backend work
already started may still consume the approved budget. The app does not create
conversation history or persist results in browser storage.

Only questions are prepared: real Fabric IQ schema feeds LLM-generated DAX,
then the authenticated broker applies role/CustomData for engine RLS, then the
LLM explains filtered rows. Opaque application users are not Entra identities.
The delegated IQ user, certificate query service principal and LLM developer
credential are separate identity contexts.

The default-visible timeline displays actual operation names, executing
identities, stage status, request ID and server elapsedMs. Stages without events
remain NOT RUN; client cancellation never invents backend cancellation events.
Expandable escaped-text details show schema input, generated DAX and other
backend-allowlisted diagnostics. Result provenance includes schema/query hashes
and separate planning/explanation timing, model and token fields.
``generationMs`` is labelled IQ + DAX planning duration because it includes
schema discovery, not only model inference.

The expandable plaintext log is complete and copyable by explicit user action
with a content warning. No download or cross-run persistence is added. Unknown
top-level event fields are excluded; credentials and tokens must never be
included in the backend diagnostic allowlist. Test fixtures are synthetic and
do not validate the live IQ end-to-end path.

Validation scope
----------------

Vitest and React Testing Library exercise the contract, lossless cell rendering,
clear/abort behavior, stale-response isolation, explicit-run behavior, disabled
configuration, cancellation and controlled service failures. Their synthetic
fixtures exist only under ``src\test`` and are not imported by the application.
These tests and a production build make no live queries or model inferences.
Browser QA against the parent-managed local host is a separate integration step.
