Scoped Analytics - presenter runbook v1
=======================================

Purpose and current evidence
-----------------------------

Show the SAME prepared question returning different semantic-model results
for different synthetic application identities, then let an LLM explain only
those authorized results. This is a technical proof on the path to portal
integration, not a complete application.

Stage 1 already demonstrated 42 live RLS checks. This runbook's complete
React -> .NET -> semantic model -> LLM sequence is a separate pending live
increment. Do not present its offline tests or the expected values below as a
recording of that integrated live run.

The selector is "demo identity - not login". Prepared questions are not
arbitrary natural-language query generation. The native results table is
not an embedded Power BI report. No Foundry Agent Service is required.

Local start (no cloud calls)
-----------------------------

From the repository root, with Windows, PowerShell 7, .NET 8 SDK and Node 24+::

    dotnet restore .\IqRls.sln --locked-mode
    npm --prefix .\src\DemoWeb ci
    pwsh -File .\tools\Start-Demo.ps1 -Build

Open ``http://127.0.0.1:5187`` (not ``localhost``). The host serves the compiled
React app from the same origin as the API. Do not use Vite's development server,
open index.html directly, proxy the application or expose the port remotely.
Without live configuration the controls are visible and Run is disabled.
``GET /api/health`` only confirms the local host; ``cloudChecked:false`` is
intentional. Stop the foreground host with Ctrl+C.

The start script never installs dependencies, creates Azure resources, resumes
capacity or authorizes inference. Subsequent launches can omit ``-Build`` if
source has not changed.

Approved live setup
-------------------

Before starting: obtain a new explicit approval for the exact development
tenant/subscription, retained synthetic workspace/model, certificate identity,
capacity lease, existing model endpoint/deployment, estimate/cost cap and
absolute deadline. Use the existing pilot lifecycle tooling, not this host,
to resume/restore capacity. Confirm resource and certificate cleanup deadlines
cover the presentation. Do not modify or extend cleanup implicitly.

Keep both configuration files and their adjacent budget ledger outside the
repository and all OneDrive folders, preferably below
``%LOCALAPPDATA%\scout\auth\iq-rls-demo``. There are two files:

* Query configuration: the Stage 1 CLI's certificate-only configuration.
  Fields are shown in ``src/IqRls.Cli/README.rst`` and
  ``LiveConfiguration.Template``. It identifies the fixed synthetic model,
  ``ExternalAppScope`` and a certificate in Windows CurrentUser/My.
* Demo configuration: fill ``src/IqRls.Demo/demo-config.example.json`` in a new
  external file, using real approved values. Its placeholders intentionally
  cannot run. Set ``liveEnabled:true`` only after approval, an absolute UTC
  ``liveUntilUtc`` ending in Z and ``maxCalls`` no greater than 100.

The model endpoint must be ``https://<resource>.openai.azure.com/`` without
query parameters. This client sends ``reasoning.effort:low`` and requires a
Responses deployment that supports that option (the intended existing
deployment is GPT-5.4). Arbitrary chat models are not interchangeable.

The LLM uses Azure CLI Entra authentication, not API keys. Its configured
tenant must match the query tenant; subscription and expected principal are
pinned. Use only the isolated development CLI context, never a corporate
M365 identity or corporate content. Existing model-inference permission is
required; a service 403 is not permission to add roles automatically.

After an approved external config exists, start::

    pwsh -File .\tools\Start-Demo.ps1 `
      -DemoConfig "$env:LOCALAPPDATA\scout\auth\iq-rls-demo\demo.local.json" `
      -AllowLive

Configuration is loaded at startup; restart after an approved change. The
UI's "Refresh configuration" reloads the host's current catalog, not the disk
file. The adjacent ``.budget.json`` persists attempts across restarts; do not
delete it or change config to reset the budget.

Presenter script (approximately 6-8 minutes)
--------------------------------------------

1. Opening: "This is synthetic data and a technical proof. The user selector
   stands in for identity our existing portal would normally supply. The
   model enforces access; the LLM does not decide what this person can see."
2. Choose Customer A - Home and the overview question. Click Run. Read the
   model rows: total 250, activity count 2. Explain that the text above them
   came from the actual LLM invocation, not a canned answer.
3. Open Technical trace. Point to ``ExternalAppScope``, ``app-user-A1``, the
   prepared DAX and query hash. The DAX contains no injected security filter.
4. Switch to Customer A - Auto. The prior answer and rows disappear; the
   question remains selected. Run again: total 100, count 2. The role and DAX
   hash remain the same; CustomData changes.
5. Choose Customer A - All products and rerun: 350 / 4. Choose Customer B -
   Home and rerun: 700 / 1. These are separate engine-scoped queries, not
   browser filtering of a full result.
6. Choose Cross-customer pair and run "Break down my activity by customer and
   product." Expect only A/Home = 250 and B/Auto = 900. This proves the
   intended pair semantics, not the Cartesian product of two customer lists
   and two product lists.
7. Still as Cross-customer pair, ask "Show Customer B's Home activity."
   Expect no rows. The question's business filter cannot enlarge RLS access.
   Change to Customer B - Home and run the same question: 700 / 1.
8. Choose No data access and run overview: 0 / 0. Then run details or
   breakdown: no rows. The explanation should say no authorized data was
   returned for this question, not reveal what another user could see.
9. Choose Customer A - Home and run the daily question: January 1 = 100,
   January 2 = 150. The LLM can explain a change of 50 (50%) but has no
   evidence for a business cause. Do not accept invented explanations.
10. Close: "The proven building block is application identity reaching real
    semantic-model RLS. This UI adds the conversational explanation. Existing
    portal login and Power BI embedding are separate integration work; we
    have not claimed report/chat parity or a production-ready solution."

Expected scenario matrix (synthetic fixtures, not new live evidence)
--------------------------------------------------------------------

======================= ========================== =========== ======= ====================
User                    Exact authorized pairs     Total       Count   Activity keys
======================= ========================== =========== ======= ====================
Customer A - Home       A/Home                     250         2       1, 2
Customer A - Auto       A/Auto                     100         2       3, 4
Customer A - All        A/Home; A/Auto              350         4       1, 2, 3, 4
Customer B - Home       B/Home                     700         1       5
Cross-customer pair     A/Home; B/Auto              1,150       3       1, 2, 6
No data access          None                       0           0       None
======================= ========================== =========== ======= ====================

Overview returns ONE summary row even for no access; its Activity Count is
the business-record count. Do not confuse that with the trace's result row
count. The row-level details query returns exactly the activity keys above.
Breakdown returns one row per listed authorized pair. The B/Home question
returns 700 / 1 only for Customer B - Home; every other demo identity returns
no rows for that question.

Daily values: A/Home = 100 then 150; A/Auto = 40 then 60; A/All = 140 then 210
when its returned pairs are summed; paired scopes = 100 then 1,050.
B/Home has a January 1 value of 700 and no returned January 2 row: a missing
date must not be described as a known zero. No-access returns no daily rows.

Full live acceptance pass
--------------------------

After approval, run all six identities against all five questions (30 runs,
60 cloud-call attempts if successful). Inspect exact rows, not just prose.
For every question compare the query hash across identities. Check zero/empty
results, the paired-scope exclusion and all daily/detail rows. Exercise user
switching while a call is in flight; an old answer must never reappear.

Each successful question consumes one semantic query and one inference.
Failures/cancellation can still consume attempts; there is no automatic retry.
An approved maximum of 100 calls permits at most 50 complete successful runs,
not 100 questions. The inference response reports model and token usage.
Retain sanitized evidence locally with source/model package revisions.
Do not record bearer tokens, raw credentials or customer content.

Operational failures
----------------------

* Disabled/expired: confirm a current approval and external config. Never
  extend the window from the browser.
* Query failure: check capacity lease, retained workspace/model, tenant API
  settings, certificate validity and scoped app permissions. No LLM is called
  after an unusable query result.
* LLM failure: check the approved deployment, developer CLI identity and
  inference permission. The UI displays no fabricated substitute.
* Budget locked/unreadable: another host may own the ledger, it may have been
  altered, or its config hash may no longer match. Resolve explicitly; do not
  erase the ledger and pretend it is unused.
* Busy: one request is still running. Cancel/wait; work already sent cannot
  necessarily be rolled back.

At the end, stop the local host, restore capacity through the approved lease,
and retain existing model/application/certificate cleanup schedules. Local
window expiry prevents more app requests; it does not stop cloud billing.
