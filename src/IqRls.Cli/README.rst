Local .NET proof harness
========================

Status: local harness only, as of 2026-09-28. No live query, model-engine RLS,
Fabric IQ custom execution, deployment, identity creation or GitHub publication
is claimed by a successful build, selftest or unit test.

Build and local checks
----------------------

From the repository root::

    dotnet restore IqRls.sln --locked-mode
    dotnet test tests\IqRls.Tests\IqRls.Tests.csproj --no-restore
    dotnet run --project src\IqRls.Cli --no-build -- selftest
    dotnet run --project src\IqRls.Cli --no-build -- --help

The solution targets .NET 8. ``global.json`` pins the installed SDK 9.0.318
toolchain (latest patch roll-forward); the target runtime is still .NET 8.
Packages are pinned and transitive resolutions are recorded in lock files.
Apache.Arrow and Apache.Arrow.Compression 22.1.0 implement Arrow/LZ4_FRAME;
Azure.Identity 1.17.2 implements certificate client credentials. xUnit and
Microsoft.NET.Test.Sdk are from the existing ``dotnet new xunit`` template.

Live commands, only after separate approval and provisioning
------------------------------------------------------------

Generate placeholders on stdout::

    dotnet run --project src\IqRls.Cli --no-build -- generate-config-template

Supply a JSON file OUTSIDE this repository containing only the displayed
public IDs, thumbprint and fixed configuration fields. Unknown/duplicate
fields, omitted fields, invalid GUIDs, non-certificate auth modes, a different
role/model alias/entitlement version and alternate endpoint hosts are rejected.
No credential-bearing JSON or certificate private-key file is accepted.
The live lane is Windows only and loads one currently valid, nonexportable RSA
certificate from ``CurrentUser\My``. It never creates or installs a certificate.
There is no DefaultAzureCredential, delegated fallback, token persistence or
ambient Azure CLI identity dependency.

After an operator has separately approved the exact resources and configured
the synthetic model, application, certificate and permissions::

    dotnet run --project src\IqRls.Cli --no-build -- query --config <absolute-external-config> --subject app-user-A1 --dax-file <synthetic-query-file> --allow-live
    dotnet run --project src\IqRls.Cli --no-build -- verify-rls --config <absolute-external-config> --allow-live

``--allow-live`` prevents accidental invocation; it is NOT a substitute for
organizational approval. The application must be Admin of its isolated model
workspace to select a role. Required tenant API/XMLA settings and capacity/API
availability must be checked before live testing. Never give this proof
application access to unrelated real-data workspaces.

The verifier sends 42 queries, spaced by two seconds, without automatic retry.
Every subject uses the same unfiltered total/count query, direct Scope and
Activity projections, ALL and REMOVEFILTERS, an explicitly denied scope, and
ALL(User Access) enumeration. Expected subject totals are A1=250, A2=100,
A3=350, B1=700, pairs=1150 and none=0. The pairs subject has exact scope keys
1 and 4, not a customer/product Cartesian product. An empty subject selection
or unknown key fails before token acquisition.
Activity key, scope key, date key and amount are compared to all six exact
synthetic fixture rows, not just aggregate sums. A test compares embedded
trusted constants against ``model\fixtures.json`` so fixture drift fails locally.
The query intentionally COALESCEs empty raw measures (BLANK) to zero.

Security and integration boundary
---------------------------------

``--subject`` is a developer choosing a fixed server fixture, NOT an
authenticated browser identity. A production gateway must authenticate and
resolve issuer/subject plus entitlement version before constructing context.
Never expose this CLI fixture resolver as a public authentication endpoint.
``ServerContext`` has no public constructor or setters. ``DaxTool`` captures
that context; its only external input is ``query``. Host, dataset, workspace,
role and CUSTOMDATA are never LLM-selectable. There is no result cache.

Each request gets its own Authorization header and immutable context values.
The CLI disables redirects and cookies. The broker always sends::

    POST https://api.powerbi.com/v1.0/myorg/groups/{workspaceId}/datasets/{datasetId}/executeDaxQueries
    {"query":"...","roles":["ExternalAppScope"],"customData":"<trusted fixture subject>","queryTimeout":30,"resultSetRowCountLimit":1000}

It never sends effectiveUsername or falls back to executeQueries. DAX is not
validated by a purported regex security parser. Model RLS is the row-security
boundary: ALL and REMOVEFILTERS must not expand it. Arbitrary DAX can expose
model metadata; this proof does not provide OLS, a general DAX sandbox, or an
approved production query policy. Multiple resultsets are outside the contract.

Output is stdout JSON, not saved files. Only synthetic result output may be
redirected to artifacts. No token, private key, raw HTTP error body, SDK inner
exception, FaultCode or FaultString is printed. Error codes are intentionally
generic; host-level SDK HTTP tracing must not be enabled with sensitive data.

Arrow contract and deliberate strictness
----------------------------------------

The HTTP body is buffered up to 8 MiB. A small defensive framing reader
preflights message envelopes using the official Arrow Message FlatBuffer field
ordinals (header discriminator=1, body length=3), before Apache.Arrow decodes
schemas and arrays. It does not pretend to be a full FlatBuffers verifier.
Supported metadata versions are V4/V5. Both modern continuation framing and
legacy four-byte framing are tested.

Every stream must start with a schema and end with an explicit EOS. A missing
EOS, truncated prefix/body, mid-stream schema, trailing junk or more than
16 concatenated streams is rejected. This is stricter than Arrow's allowance
of EOF as EOS, intentionally: the service returns concatenated streams, and
silently accepting EOF could release partial query results. The Apache reader
is bounded to each preflighted stream; consumed length is checked afterward.

All streams are decoded before any rows are returned. IsError=true anywhere,
including after a data stream, rejects the complete response. Exactly one data
stream is permitted. Empty rows WITH a valid schema/EOS are allowed; no stream,
an EOS alone or an empty schema is not. No catch-and-break partial success.

There are 64-column, 16,384-character/cell, 2-Mi-character/value-text and 32-MiB
cumulative Arrow allocation limits. A result reaching the 1,000-row service cap
is rejected because truncation cannot be ruled out. Unknown Arrow types fail
closed rather than silently stringify. Supported scalar types are signed and
unsigned integers, finite float/double, boolean, UTF-8, null, Decimal128,
Date32/Date64, timestamp and dictionaries of supported values.

Each column has an explicit Arrow type descriptor. Values are invariant
strings, or JSON null, preserving all int64 digits and Decimal128 coefficient/
scale rather than losing precision through JSON double coercion. Dates are
ISO calendar dates. Timestamp values retain exact epoch units and timezone in
the descriptor, including nanoseconds; no precision-losing DateTime conversion.

The broad exception scrub is limited to the third-party Arrow parser, injected
authentication boundary and final CLI output boundary: Arrow can throw even
plain System.Exception for malformed data. Failures are never treated as EOF.

Public sources verified 2026-09-28
---------------------------------

The parent refreshed ``microsoft/skills-for-fabric`` to commit
``6c11ad58c25992e5d1435ce7cd80d217d5598a31`` before this work. References read
for the query/fixture contract: ``skills/semantic-model-authoring/SKILL.md``,
``skills/semantic-model-authoring/references/dax-guidelines.md``, and
``common/ITEM-DEFINITIONS-CORE.md``. Model-specific provenance is also recorded
in ``model\references.json``. The negative tests intentionally use both ALL
and REMOVEFILTERS rather than substituting one for the other.

* ExecuteDaxQueries REST v1.0, including SP role-selection restrictions,
  concatenated streams and HTTP-200 error rowsets:
  https://learn.microsoft.com/en-us/rest/api/power-bi/datasets/execute-dax-queries-in-group
* Azure.Identity certificate constructor and token acquisition:
  https://learn.microsoft.com/en-us/dotnet/api/azure.identity.clientcertificatecredential?view=azure-dotnet
  https://learn.microsoft.com/en-us/dotnet/api/azure.identity.clientcertificatecredential.-ctor?view=azure-dotnet
* Apache .NET documentation and verified 22.1.0 reader API:
  https://arrow.apache.org/dotnet/current/index.html
  https://github.com/apache/arrow-dotnet/blob/v22.1.0/src/Apache.Arrow/Ipc/ArrowStreamReader.cs
* Official Arrow framing/Message and LZ4 codec implementation:
  https://arrow.apache.org/docs/format/Columnar.html#ipc-streaming-format
  https://github.com/apache/arrow/blob/main/format/Message.fbs
  https://github.com/apache/arrow-dotnet/blob/v22.1.0/src/Apache.Arrow.Compression/Lz4CompressionCodec.cs

The REST docs mention DaxQueryArrowResponseReader without establishing a
usable SDK package/version here; this implementation deliberately uses the
verified HttpClient and Apache.Arrow APIs instead.

Outstanding integration decisions
---------------------------------

Exact cloud scope/IDs, approval, certificate provisioning, tenant API settings,
model deployment/refresh and live app-only acceptance remain with the operator.
Fabric IQ custom-execution binding and authenticated issuer/subject resolution
are future integrations, not simulated completed capabilities. Dependency
upgrades or changes to service framing require rerunning the negative parser
tests and then separately approved synthetic live verification.
