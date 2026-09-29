Scoped Analytics - technical presenter runbook v2
=================================================

Historical pre-live setup narrative. For the working educational version, use
``quickstart.rst``, ``status.rst`` and ``customer-overview_v2.rst``. This file
is retained to explain the original prepared-question and inspector design.

This version supersedes v1's fixed-DAX runtime design. Prepared QUESTIONS
simplify the presenter interface; they do not bypass Fabric IQ or DAX generation.
The current flow is live IQ schema -> LLM-generated DAX -> custom authenticated
query broker -> model-enforced RLS -> explanation. No static DAX or schema
fallback is acceptable evidence.

Audience and evidence boundary
-------------------------------

This is for technical people who need to see the calls and identity boundaries,
not just a polished answer. Stage 1 passed 42 live RLS checks. The complete
IQ/generation/execution/explanation path must be demonstrated separately under
a new approved live window; offline transport fixtures are not live evidence.

The app user is application-owned, not an Entra account. In this proof the
selector replaces application login. The same opaque key exists in the model's
entitlement mapping. Distinguish it from the IQ delegated Entra user, the
certificate-authenticated query service principal and the LLM service caller.

Local start
-------------

From the repository root with Windows, .NET 8 SDK, Node 24+ and PowerShell 7::

    dotnet restore .\IqRls.sln --locked-mode
    npm --prefix .\src\DemoWeb ci
    pwsh -File .\tools\Start-Demo.ps1 -Build

Open ``http://127.0.0.1:5187``. Without approved external configuration Run
is disabled. The visible execution path is a NOT RUN plan, not a recording of
service calls. Do not use localhost, a Vite origin or a remote proxy.

Live prerequisites
--------------------

The Stage 1 approval expired and did not cover IQ delegated setup or inference.
Approve the actual development tenant/subscription, fixed synthetic model,
existing certificate, IQ delegated client/consent/user, model deployment,
capacity lease, absolute deadline, call/token ceilings and cost cap.
No resource or permission creation is implied by a working local build.

External query/demo configuration and all auth state belong outside the repo
and OneDrive. Start from ``src/IqRls.Demo/demo-config.example.json`` with
approved real values. IQ requires delegated Power BI Service permissions:
``Item.Read.All``, ``Item.Execute.All`` and ``Dataset.Read.All``. A certificate
service principal is not a supported IQ identity. Do not try to fix IQ by
substituting the selected application user or corporate work credentials.

The model endpoint is a Responses-capable approved Azure OpenAI deployment.
The intended existing deployment is GPT-5.4. The app does not need Foundry
Agent Service. Sign-in and configuration are explicit operator setup, not
browser actions triggered by a demo question.

The external ``iq`` configuration has ``tenantId``, ``clientId`` and
``expectedPrincipal``. The client is an approved delegated public-client
registration, not the certificate application's client ID by assumption.
After the required registration/permissions are approved and configured,
the operator explicitly signs in once::

    pwsh -File .\tools\Start-Demo.ps1 `
      -DemoConfig "$env:LOCALAPPDATA\scout\auth\iq-rls-demo\demo.local.json" `
      -IqSignIn

This command is separate from serving the app. It opens a browser for the
configured development account and saves only locally protected auth state.
Do not combine ``-IqSignIn`` with ``-AllowLive``. Normal demo requests never
launch interactive sign-in. Missing or expired delegated access is a visible
failure, not permission to use a service principal or another tenant.

After setup and approval, start::

    pwsh -File .\tools\Start-Demo.ps1 `
      -DemoConfig "$env:LOCALAPPDATA\scout\auth\iq-rls-demo\demo.local.json" `
      -AllowLive

The host loads config at startup. "Refresh configuration" reloads the running
host's catalog; restart after an approved disk-config change. Do not delete or
edit the persistent budget ledger to restart spending. MCP lifecycle and tool
calls now consume budget in addition to generation, query and explanation.
One question is no longer two cloud calls; plan the acceptance run accordingly.
The usual path is seven budgeted service requests: initialize, initialized
notification, tools/list, schema tool call, DAX generation, scoped query and
explanation. Extra tools/list pages add calls. A 100-call window fits at most
14 complete usual-path runs, not all 30 user/question combinations. Split the
full acceptance matrix into separately approved windows; never reset the
ledger to squeeze it into an exhausted approval.

Walk through the actual execution
----------------------------------

1. Select Customer A - Home and overview. Explain: "This is app-user-A1,
   not an Entra user. The selector simulates our portal's identity decision."
2. Click Run. Watch the execution panel receive real events. Explain that
   timestamps, sequence and request ID come from the backend, not a progress
   animation. Open detail sections as stages complete.
3. Show the IQ credential category. It is a delegated Entra metadata caller.
   Show MCP initialization, version header, tools/list and the allowed
   GetSemanticModelSchema call. Native ExecuteQuery AND ValueSearch are
   excluded from dispatch under this identity.
4. Show the reviewed schema passed to generation. It is retrieved live and
   projected to the known synthetic model's approved metadata, not hard-coded
   table results, entitlement rows or arbitrary AI instructions.
5. Show the DAX-generation Responses call and the exact generated query.
   "IQ supplies schema; the LLM writes DAX. Neither chooses this user's
   permissions." Inspect its hash and model/token details.
6. Show the custom broker's executeDaxQueries request. Its role is fixed
   ExternalAppScope and its customData is app-user-A1, taken from the
   immutable app context. Authentication here is a separate service principal
   with a certificate. No access token or certificate material is displayed.
7. Explain model enforcement: CUSTOMDATA() resolves exact permitted
   customer/product pairs, restricts Scope and propagates to Activity. The
   broker does not rely on the LLM inserting a customer security predicate.
8. Show Arrow parsing completed, exact returned row count, then the separate
   explanation call. Only the current question and authorized rows reach this
   call. Read the source rows before trusting the prose.
9. Change user and rerun the same question. Old events, schema, DAX, rows and
   answer clear immediately. IQ/generation runs again. The generated query
   hash MAY differ: inspect logical equivalence rather than promising
   byte-identical DAX. Stage 1's deterministic harness is the stronger
   same-query isolation proof.
10. Open the current-run event log. Correlate request ID to the backend console
    lifecycle lines. The UI contains more detail; console logs intentionally
    omit payload values. The "Copy complete event log" button is explicit;
    it warns that the log may include schema, generated DAX and the synthetic
    app-user context. Keep a copied diagnostic log local unless reviewed for sharing.

Different-user scenarios
-------------------------

Expected fixture outcomes are not a claim of a completed new live run:

======================= ========================== =========== ======= ====================
User                    Exact authorized pairs     Total       Count   Activity keys
======================= ========================== =========== ======= ====================
Customer A - Home       A/Home                     250         2       1, 2
Customer A - Auto       A/Auto                     100         2       3, 4
Customer A - All        A/Home; A/Auto              350         4       1, 2, 3, 4
Customer B - Home       B/Home                     700         1       5
Cross-customer pair     A/Home; B/Auto              1,150       3       1, 2, 6
No data access          None                       0/BLANK     0/BLANK None
======================= ========================== =========== ======= ====================

An overview can return a single summary row even with no business records.
BLANK versus explicit zero depends on the generated DAX's COALESCE behavior.
The trace's result-row count is not Activity Count. Inspect actual DAX and
schema/measure semantics; do not substitute a canned zero response.

For paired scopes, breakdown must not include A/Auto or B/Home. Ask "Show
Customer B's Home activity" as the paired user: no authorized data for that
question. Switch to Customer B - Home and ask again: total 700, count 1.
A business filter cannot enlarge model RLS access.

Daily: A/Home is 100 on January 1 and 150 on January 2; A/Auto is 40 then 60;
A/All aggregates to 140 then 210; paired scopes aggregate to 100 then 1,050.
B/Home has 700 only on January 1. A missing January 2 row is not necessarily
a known zero. LLM prose must not invent a business cause.

Failure demonstration and acceptance
--------------------------------------

An IQ/auth/generation failure must stop before query execution. A query error
must stop before explanation. Completed stages stay completed; failed or
unreached stages are explicitly distinguished. HTTP 200 for a streaming
request is not success: inspect its final result/error frame.

For each of six users and five questions, retain actual schema hash, generated
DAX, engine rows, timings, model/token usage and safe correlation ID. Compare
the returned activity keys/pairs to the matrix, not just the answer's wording.
If generation makes a wrong business query, surface the problem; do not replace
it silently with static DAX. A different result caused by a business predicate
is not proof of a different authorization scope.

Test selection changes during IQ/generation and during query/explanation.
Late events and answers must not repopulate the new user's screen. No results
or conversation history persist in browser storage.

The local host never resumes/pauses capacity or extends cleanup deadlines.
At the end stop it and restore capacity through the approved pilot lifecycle
lease. Existing model/application/certificate cleanup schedules remain in force.
Window expiry stops app requests, not cloud billing.

Not claimed
-------------

No application login, end-user Entra provisioning, arbitrary free-text UI,
revocation workflow, actual report iframe, Embedded/chat parity, live IQ
app-only support, Foundry Agent Service or production readiness.
