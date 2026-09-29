Deploy the sample in your development tenant
============================================

Baseline: **28 September 2026**. These instructions describe the checked-in
runtime, not a provisioning product. Use your own development tenant, an
isolated synthetic workspace and your organization's normal permission to
create resources and incur costs. No Scout installation or author-local lab
state/cleanup scripts are required.

For the UI without services, start with the `quickstart <quickstart.rst>`_.
For why the identities are separate, read the `architecture <architecture.rst>`_.

1. Prerequisites
----------------

* Windows, PowerShell 7 (``pwsh``), Azure CLI and Node.js 24+ with npm.
* .NET 8 and ASP.NET Core 8 runtime support. An installed .NET 8 SDK supplies
  those runtimes, but **also satisfy the repository SDK pin**:
  `global.json <../global.json>`_ currently selects ``9.0.318`` with
  ``latestPatch`` roll-forward. An 8.x SDK alone cannot build this checkout.
* A development Entra tenant where you can register applications or have an
  administrator do so, plus an account permitted to use Fabric and the model.
* An existing, available capacity suitable for the semantic-model APIs, with
  permission to assign an isolated workspace. Confirm current licensing,
  tenant settings and API availability; do not infer every capacity SKU works.
* Access to an Azure OpenAI resource with a GPT-5.4 deployment supporting the
  Responses API and structured outputs. The current code accepts public-cloud
  ``https://<resource>.openai.azure.com`` endpoints, not arbitrary compatible
  endpoints or API-key configuration.

Useful checks from the repository root::

    dotnet --version
    dotnet --list-runtimes
    node --version
    pwsh --version
    az version

Review the current `Fabric IQ MCP prerequisites
<https://learn.microsoft.com/en-us/fabric/iq/connectors/fabric-iq-mcp>`_ and
`executeDaxQueries requirements
<https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/execute-dax-queries-in-group>`_.
The latter's Admin requirement matters to this design; a generic successful
Fabric API call does not establish query-role support.

2. Package the complete synthetic model
---------------------------------------

From the repository root::

    dotnet restore .\tools\model-definition\ModelDefinition.csproj --locked-mode
    dotnet run --project .\tools\model-definition\ModelDefinition.csproj --no-restore -- validate
    dotnet run --project .\tools\model-definition\ModelDefinition.csproj --no-restore -- package

The package command prints the content hash and output directory. It writes::

    artifacts\model-definition\packages\<hash>\createSemanticModel.json
    artifacts\model-definition\packages\<hash>\approval.manifest.json

``createSemanticModel.json`` is the full Create Semantic Model body: TMDL parts,
``definition.pbism``, relationships and both roles, encoded as ``InlineBase64``.
The manifest records exact source/request hashes; its filename does not imply
that another organization has approved your deployment.

These tools do **not** sign in, create a workspace, deploy, refresh or run DAX.
TOM parsing is not semantic-engine execution. All data comes from inline
synthetic M tables; no external data source or gateway is needed.
The model-definition project is separate from ``IqRls.sln`` and needs its own
restore.

3. Create a workspace, deploy, then refresh
-------------------------------------------

In the Fabric portal, create a new workspace used only for this demo and assign
it to your existing capacity. Keep real data out. Record the workspace and
capacity IDs in your local operator notes, not in the repository.
See `Create a workspace
<https://learn.microsoft.com/en-us/fabric/fundamentals/create-workspaces>`_.

Publish the packaged body using a REST client authenticated as your deployment
operator. This is separate from the runtime's query certificate. The operation
requires the documented delegated create scope and appropriate workspace
permissions; see `Create Semantic Model
<https://learn.microsoft.com/en-us/rest/api/fabric/semanticmodel/items/create-semantic-model>`_.
Use the Fabric token audience, not a Power BI, OpenAI or embed token::

    Token audience: https://api.fabric.microsoft.com
    POST https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/semanticModels
    Content-Type: application/json
    Body: the complete contents of createSemanticModel.json

Use a client that retains the response status and headers. Do not print or
persist authorization headers. For a synchronous ``201 Created``, record the
returned semantic-model ``id``. For ``202 Accepted``:

1. Read ``Location``, ``x-ms-operation-id`` and ``Retry-After``.
2. Poll the returned operation location, waiting as directed::

       GET https://api.fabric.microsoft.com/v1/operations/{operationId}

3. Continue until ``Succeeded``; stop on failure or your own deployment timeout.
   ``202`` by itself is not successful deployment.
4. Retrieve the result and record the created model's ID::

       GET https://api.fabric.microsoft.com/v1/operations/{operationId}/result

Use the same Fabric audience for polling. Consult
`Fabric long-running operations
<https://learn.microsoft.com/en-us/rest/api/fabric/articles/long-running-operation>`_
for the precise response contract. Do not repeatedly submit the create request
while its operation is running.

The semantic-model item ID is the ``datasetId`` used by the Power BI API.
Verify its identity in the isolated workspace; for example::

    GET https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/semanticModels

Follow pagination if returned. This repository contains the semantic-model
portion of a project, **not** a complete report-launching PBIP/PBIX; do not
expect to upload the folder as a report in the portal.

**Refresh is required.** Creating the definition does not populate Import
partitions. In the portal, refresh the created semantic model and inspect its
refresh history, or use the Power BI refresh API with its different audience::

    Token audience: https://analysis.windows.net/powerbi/api
    POST https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}/datasets/{datasetId}/refreshes
    Content-Type: application/json

    {"notifyOption":"NoNotification"}

    GET https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}/datasets/{datasetId}/refreshes?$top=1

Wait for the refresh to complete, matching the submitted refresh rather than
mistaking an older success for this one. See `Refresh Dataset In Group
<https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/refresh-dataset-in-group>`_
and `Get Refresh History In Group
<https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/get-refresh-history-in-group>`_.
Only then is the model ready for a query.

4. Create the certificate-authenticated query principal
-------------------------------------------------------

Create a dedicated single-tenant Entra app registration for the query broker.
Use a certificate, not a client secret. On the same Windows account that will
run the demo, create a short-lived nonexportable RSA key and export **only the
public certificate** outside the repository::

    $local = Join-Path $env:LOCALAPPDATA 'fabric-iq-rls'
    New-Item -ItemType Directory -Path $local -Force | Out-Null
    $cert = New-SelfSignedCertificate `
      -Type Custom -Subject 'CN=FabricIqRlsDemo' `
      -CertStoreLocation 'Cert:\CurrentUser\My' `
      -Provider 'Microsoft Software Key Storage Provider' `
      -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
      -KeyUsage DigitalSignature -KeyExportPolicy NonExportable `
      -NotAfter (Get-Date).AddDays(7)
    Export-Certificate -Cert $cert -FilePath (Join-Path $local 'query-public.cer') | Out-Null

Upload ``query-public.cer`` under the app registration's Certificates & secrets
page. Keep the thumbprint in your local query configuration. The runtime checks
for one currently valid certificate with a private key in ``CurrentUser\My`` and
rejects exportable keys. Do not export a PFX or copy the private key to the repo.
See `Create a self-signed certificate
<https://learn.microsoft.com/en-us/entra/identity-platform/howto-create-self-signed-certificate>`_.

Have the Fabric administrator enable the applicable service-principal API tenant
setting for a narrowly scoped security group containing this principal. Grant
the principal **Admin of this isolated workspace only** for
``executeDaxQueries`` role selection. It does not need subscription-wide Admin.
Do not add broad delegated user permissions to this app as an authentication
shortcut. Follow the service-principal setup and tenant-setting guidance in
`Power BI service-principal authentication
<https://learn.microsoft.com/en-us/power-bi/developer/embedded/embed-service-principal>`_
alongside the query operation's requirements.

Admin is deliberately powerful: dropping the selected role could expose the
whole model. The backend must always attach ``ExternalAppScope`` and the trusted
subject. Do not grant this principal access to unrelated workspaces.

5. Register the delegated Fabric IQ client
------------------------------------------

Create a **different**, single-tenant public-client app registration:

* Authentication platform: Mobile and desktop applications.
* Redirect URI: ``http://localhost``.
* Public-client flow enabled as required by the registration.
* Power BI Service **delegated** permissions: ``Item.Read.All``,
  ``Item.Execute.All`` and ``Dataset.Read.All``; obtain applicable consent.
* Use your development work/school account with the model read/build access
  required by IQ. Enable any required IQ tenant setting for that account.

This client has no secret. The runtime uses ``InteractiveBrowserCredential``,
not the Azure CLI or the query service principal, for IQ. These scopes are
endpoint-wide: allowing only ``GetSemanticModelSchema`` is an application
policy, not a metadata-only OAuth scope.

The checked-in runtime requires the IQ and LLM expected user to match, all cloud
credentials to share the configured tenant, and rejects corporate
``@microsoft.com`` IQ users. Diagnostic labels may still say "MCAPS DEV"; no
specific MCAPS tenant is required. Use your own development tenant and account.

6. Configure Azure OpenAI inference
-----------------------------------

Use your own GPT-5.4 deployment and give the development caller
``Cognitive Services OpenAI User`` on that resource. Configure its resource
endpoint and deployment **name**, not a model-family name assumed to exist.
See `Azure OpenAI role-based access
<https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/role-based-access-control>`_
and the `Responses guide
<https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/responses>`_.

Sign in to Azure CLI for your development tenant, select the intended
subscription, and run the host from that same shell. An optional dedicated
``AZURE_CONFIG_DIR`` under local application data keeps this separate from
other CLI accounts::

    $env:AZURE_CONFIG_DIR = Join-Path $env:LOCALAPPDATA 'fabric-iq-rls\azure-cli'
    az login --tenant '<development-tenant-guid>'
    az account set --subscription '<development-subscription-guid>'

The application requests ``https://ai.azure.com/.default``. It sets only
``AzureCliCredentialOptions.Subscription`` and checks the returned tenant and
principal claims; passing both tenant and subscription to Azure CLI token
acquisition is rejected by CLI. No API key or default credential-chain fallback
is implemented. Foundry Agent Service is not needed.

7. Put both configurations outside the repository
-------------------------------------------------

Use an absolute local path, such as
``C:\Users\<you>\AppData\Local\fabric-iq-rls``. UNC paths, OneDrive paths, repository
paths and reparse-point paths are rejected. Do not publish populated config or
auth files. Replace every angle-bracket placeholder below with your own value.

``query.local.json``::

    {
      "authMode": "certificate",
      "tenantId": "<development-tenant-guid>",
      "clientId": "<query-application-client-guid>",
      "workspaceId": "<isolated-workspace-guid>",
      "datasetId": "<semantic-model-guid>",
      "certificateThumbprint": "<40-hex-current-user-certificate-thumbprint>",
      "modelAlias": "synthetic-rls-v1",
      "entitlementVersion": "synthetic-v1",
      "role": "ExternalAppScope"
    }

``demo.local.json``::

    {
      "liveEnabled": true,
      "liveUntilUtc": "<your-session-end-in-UTC-YYYY-MM-DDTHH:mm:ssZ>",
      "maxCalls": 100,
      "queryConfigPath": "C:\\Users\\<you>\\AppData\\Local\\fabric-iq-rls\\query.local.json",
      "iq": {
        "tenantId": "<development-tenant-guid>",
        "clientId": "<IQ-public-client-guid>",
        "expectedPrincipal": "<development-user-UPN>"
      },
      "llm": {
        "endpoint": "https://<your-resource>.openai.azure.com",
        "deployment": "<your-GPT-5.4-deployment-name>",
        "tenantId": "<development-tenant-guid>",
        "subscriptionId": "<development-subscription-guid>",
        "expectedPrincipal": "<same-development-user-UPN>"
      }
    }

Use a future but bounded UTC end time and ``maxCalls`` between 1 and 100. These
are strict JSON shapes: do not add role/endpoint overrides or credential fields.
Keep model alias, entitlement version and role exactly as shown for this model.

8. Sign in explicitly, then run
-------------------------------

After the local build in the quickstart::

    $demoConfig = Join-Path $env:LOCALAPPDATA 'fabric-iq-rls\demo.local.json'
    pwsh -File .\tools\Start-Demo.ps1 -DemoConfig $demoConfig -IqSignIn
    pwsh -File .\tools\Start-Demo.ps1 -DemoConfig $demoConfig -AllowLive

The first command opens a browser for IQ sign-in and exits without querying the
model. The second serves the demo. Do not combine the switches. The executable
equivalents are ``--iq-sign-in`` and ``--allow-live``.

IQ auth state is DPAPI-protected under
``%LOCALAPPDATA%\scout\auth\fabric-iq\<tenantId>\<clientId>``. The ``scout`` folder
name is a current implementation detail, not a dependency on the Scout app.
Normal question requests use cached sign-in and never open a login browser.

Open ``http://127.0.0.1:5187`` and follow the learning guide's selected scenarios.
Check the live IQ events, generated query, returned rows and explanation.
The health endpoint checks the local host only; it does not probe cloud access.

Cost, limits and cleanup
------------------------

The host does not create, resume, pause or delete capacity. Stopping it or
reaching ``liveUntilUtc`` stops new demo calls, **not capacity billing**.
Arrange your own capacity/inference budget and cleanup time before starting.

The adjacent ``demo.local.json.budget.json`` ledger persists attempted service
calls for this exact configuration. A normal question consumes seven attempts;
100 calls fit at most 14 complete normal-path runs, not all 30 combinations.
Failures/cancellation can still spend attempts. Restarts do not reset the
ledger; do not edit/delete it or change config to bypass its limit.
The app allows one request at a time, spaces calls by two seconds and bounds
request time. This is not a monetary spending limit.

After your session: stop the host; remove only the demo workspace, app
registrations/permissions and certificate you created; remove local sample auth
state when no longer needed; and release demo-owned capacity/inference resources
according to your plan. Do not delete shared capacity or pause it while others
need it. Check billing independently. Author-local lifecycle scripts are not
included and must not be assumed to protect a community deployment.

Troubleshooting without weakening the boundary
----------------------------------------------

* SDK not found: satisfy ``global.json`` and install the .NET 8 runtimes.
* UI works but Run is disabled: check live flags, expiry and remaining budget.
* ``iq_auth_required``: finish the public-client setup and explicit sign-in.
  Do not substitute app-only IQ authentication.
* ``iq_identity_rejected`` / ``llm_auth_failed``: check the configured account,
  tenant, subscription, scopes and resource role without printing tokens.
* Query failure: check model refresh, capacity, query principal, certificate
  and API requirements. Never remove the fixed role to "get it working."
* ``iq_schema_unrecognized``: inspect the adapter contract. Do not invent
  omitted fields or switch to offline metadata.
* Empty but unexpected result: inspect generated business filters before
  diagnosing a permissions problem. Authorization and query intent differ.
* Budget unavailable/exhausted: stop; preserve the ledger. Browser refresh only
  reloads the running host's catalog, not disk config or spending state.
