Staged live deployment
=====================

Status: no live deployment completed.

Gate 0 - actual operator approval
---------------------------------

Freeze the deployment specification and model package hash. Preview the exact
target tenant/subscription, workspace/capacity, operations, principal permissions,
credential expiry, estimate, cost ceiling, billing lease and cleanup.
Do not infer these approvals from a budget limit or a passing offline test.

Use existing environment lifecycle tooling rather than inventing a second
capacity/workspace cleanup path. Keep the actual approval record and environment
identifiers in an operator-local directory, not this public-facing repository.

The operator must verify the active token's tenant and principal, not just the
Azure CLI account display. Generic ``az rest`` may acquire a token for a
different tenant than intended. Never output access tokens to diagnose this.

Gate 1 - semantic engine correctness
------------------------------------

1. Establish a working expiry backstop before resuming billable capacity.
2. Create one isolated short-lived pilot workspace on the approved capacity.
3. Publish the exact complete model package and perform Import processing.
4. Create/use the separately approved application identity; grant Admin only
   in this pilot workspace if needed for role selection, not subscription-wide.
5. Run the deterministic proof CLI with an app-only credential and every
   required role/custom-data field.
6. Capture exact expected/actual row-key sets and safe correlation identifiers.
7. Stop on any cross-scope data, missing security context, malformed result,
   unrestricted retry or undocumented transport fallback.
8. Pause/restore capacity according to its approved lease and other owners.

The certificate private key should remain nonexportable in the local current-user
certificate store for the initial proof. The repository may document a
thumbprint setting, but must not contain a key, access token or refresh token.
Hosted credential design is a later gate, not implicit reuse of a personal login.

Gate 2 - Embedded parity
------------------------

Create the synthetic report and generate a real embed token from the same
trusted authorization decision. Compare exact scope/activity rows and totals
for every subject, not merely the token request payload. Respect visual export
limitations. Do not claim parity until this has run.

Gate 3 - IQ and Foundry
-----------------------

Capture and review actual IQ tools/list and metadata outputs. Keep native data
tools unavailable. Complete the Foundry function-call round trip with hidden
request context. Test prompt/argument tampering, cross-user conversation IDs,
sequential and parallel request reuse, revocation and failures.

Gate 4 - hosted application / external MCP
------------------------------------------

Select a real application identity provider and authenticated hosting model.
Developer fixture identity selection MUST NOT be remotely exposed.
Obtain a new exact cost/TTL/operations approval for hosting. An external MCP
facade must authenticate its own audience and derive current entitlements.

Iteration and teardown
-----------------------

Changes to approved security definitions, targets, credential permissions,
costs or TTL require reapproval. Preserve previous evidence with its artifact
hash. Never re-label offline fixtures as live outcomes.

Cleanup must remove only resources owned by this pilot. Do not delete a shared
capacity/resource group or pause capacity now owned by another active lease.
Budget alerts are not hard service spending limits. Document whether each
backstop survives session termination, workstation sleep and network failure.

GitHub publication
-------------------

Start private. Preview the destination and exact staged content before a push.
Include synthetic source, tests, architecture, setup, limitations and observed
results. Exclude corporate research, customer identities/documents, environment
config, secrets, approval records and raw live traces.

Before public release, choose the license, preserve notices, review support
claims, run a clean-room setup and obtain explicit visibility/release approval.
