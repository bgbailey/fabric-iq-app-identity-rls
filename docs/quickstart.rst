Quickstart
==========

Two different outcomes
----------------------

**Local UI:** explore the six subjects, five questions and inspector without
cloud configuration. Run is disabled. No synthetic answer is shown as if live.

**Working live demo:** IQ retrieves schema, GPT-5.4 generates DAX, Power BI
executes with RLS, and GPT-5.4 explains returned rows. This requires your own
services, permissions and credentials. The local UI alone does not establish
that they work.

1. Build and open the UI
------------------------

Use Windows, PowerShell 7, Node.js 24+ and .NET 8/ASP.NET Core 8 runtime support.
Also install the SDK selected by `global.json <../global.json>`_: currently
``9.0.318`` with latest-patch roll-forward. The target is ``net8.0``, but an
8.x SDK alone does not satisfy that pin.

From the repository root::

    dotnet restore .\IqRls.sln --locked-mode
    npm --prefix .\src\DemoWeb ci
    pwsh -File .\tools\Start-Demo.ps1 -Build

Package restore needs network access; these commands do not provision cloud
resources. Open **http://127.0.0.1:5187** exactly, not an alternate localhost
hostname, Vite origin or remote proxy. Stop the foreground host with Ctrl+C.

The user selector is a demo control, not login. A planned stage marked NOT RUN
is not a recording of a service call. ``/api/health`` reports local host health,
not successful IQ, Power BI or inference access.

2. Prepare your live environment
--------------------------------

Follow `deployment <deployment.rst>`_ in order:

1. Create an isolated workspace on your existing compatible Fabric capacity.
2. Package the TMDL, deploy it through Fabric REST, wait for completion, then
   refresh the Import model.
3. Register the query service principal, upload its public certificate and
   grant Admin only in that workspace. Keep the private key nonexportable in
   the current Windows user's certificate store.
4. Register a separate IQ single-tenant public client with ``http://localhost``
   redirect and delegated ``Item.Read.All``, ``Item.Execute.All`` and
   ``Dataset.Read.All``. Enable the required tenant access.
5. Prepare your Azure OpenAI GPT-5.4 deployment and give the development caller
   ``Cognitive Services OpenAI User``. Sign in to the matching Azure CLI context.
6. Fill in the two external JSON files from the deployment guide. Use your own
   tenant/resource values, bounded expiry and at most 100 service-call attempts.

No Foundry Agent Service, app-user Entra accounts, actual Embedded report,
Scout installation or unpublished operator script is needed.

3. Sign in, start and inspect
-----------------------------

Stop the offline host first. From the same development-account shell::

    $demoConfig = Join-Path $env:LOCALAPPDATA 'fabric-iq-rls\demo.local.json'
    pwsh -File .\tools\Start-Demo.ps1 -DemoConfig $demoConfig -IqSignIn
    pwsh -File .\tools\Start-Demo.ps1 -DemoConfig $demoConfig -AllowLive

``-IqSignIn`` opens the IQ sign-in browser and exits. ``-AllowLive`` starts the
host; do not combine them. Use the configured development user, not the
application subject selected in the portal.

Try this short sequence:

* A1 + overview: expect total 250 and activity count 2.
* B1 + overview: expect 700 and count 1.
* Paired subject + breakdown: expect A/Home 250/2 and B/Auto 900/1 only.
* Paired subject + B/Home question: expect no authorized data for that question.
* No-access subject + overview: no business records; zero or BLANK measures
  depending on the generated query.

These selected cases ran successfully on **28 September 2026**; they do not
establish all 30 subject/question combinations. See `status <status.rst>`_.
Changing subject clears the previous trace and answer. Read generated DAX and
exact rows before trusting the prose. The same question may generate different
DAX; the earlier deterministic RLS harness is the same-query proof.

4. Finish the session
---------------------

Ctrl+C stops the host, not cloud billing. Follow your
`cleanup plan <deployment.rst#cost-limits-and-cleanup>`_. Keep configuration,
certificates and auth caches outside the repository and synced folders.
The call ledger survives restarts; do not reset it to extend a session.

Next: `five-minute explanation and code tour <learning-guide.rst>`_,
`trust boundaries <architecture.rst>`_, or
`comparison with Embedded <power-bi-embedded-comparison.rst>`_.
