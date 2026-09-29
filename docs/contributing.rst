Contributing
============

Help make the approach easier to understand and reproduce. This is an
educational community sample for ISV AI applications, not a production platform
or an exhaustive validation project.

Useful contributions
--------------------

* Clearer setup instructions verified in your own development environment.
* Small, synthetic examples explaining identity transport or exact-pair RLS.
* Better question grounding, query semantics or visible execution diagnostics.
* Focused fixes with a regression check using the existing test runners.
* Corrections to dated API assumptions, supported by current public references.

Begin with the `learning guide <learning-guide.rst>`_ and
`architecture <architecture.rst>`_. Keep changes small enough to explain.
Propose broader model support, login integration or hosting separately rather
than quietly widening the sample's boundary.

Keep the teaching contract intact
---------------------------------

* The selector must remain visibly a simulation, not be described as login.
* Identity, roles, credentials and physical model routing belong to the backend.
* Business-query validation is not a replacement for engine RLS.
* Do not enable native IQ data tools as a fallback for the query broker.
* Do not manufacture schema fields, query results or answers when live steps fail.
* Preserve exact customer/product pairs and the no-access example.
* Keep the call ledger and expiry controls; do not work around them to run tests.

Run the relevant existing checks
--------------------------------

From the repository root, restore pinned dependencies when needed::

    dotnet restore .\IqRls.sln --locked-mode
    npm --prefix .\src\DemoWeb ci

Choose only the checks relevant to your change::

    dotnet test .\tests\IqRls.Tests\IqRls.Tests.csproj --no-restore
    dotnet test .\tests\IqRls.Demo.Tests\IqRls.Demo.Tests.csproj --no-restore
    npm --prefix .\src\DemoWeb test
    npm --prefix .\src\DemoWeb run build
    dotnet restore .\tools\model-definition\ModelDefinition.csproj --locked-mode
    dotnet run --project .\tools\model-definition\ModelDefinition.csproj --no-restore -- test

These are offline application checks, not live RLS evidence. Documentation-only
changes need accurate links and commands, not a new test framework. Use the
SDK/runtime requirements in the `quickstart <quickstart.rst>`_.

Report evidence precisely
--------------------------

Distinguish fixture expectations, mocked/offline checks, deterministic live
engine checks and integrated live question runs. Include the date, what changed
and which scenario actually ran. A successful selected case is not a completed
30-case matrix, Embedded parity or a production-readiness claim.

Use only synthetic, reviewed examples in issues or proposed changes. Do not
attach populated environment config, real tenant/workspace/client IDs, user
addresses, certificates, auth caches, bearer headers or raw live traces.
Summarize failures with controlled error codes and redacted reproduction steps.

Licensing and project status
----------------------------

Contribute only material you may license under the project's
`MIT license <../LICENSE>`_, and preserve upstream notices.
Microsoft icons and trademarks retain their original terms; see
`THIRD_PARTY_NOTICES <../THIRD_PARTY_NOTICES>`_. No Microsoft endorsement is
implied. Deck rebuilding currently depends on author-local assets/tools and is
not a clean-checkout community build path.

At the 28 September 2026 snapshot the repository is private. These contribution
instructions do not announce a public release or imply that local changes have
been pushed.
