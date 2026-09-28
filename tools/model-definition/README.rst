Offline semantic model definition tool
=====================================

From the project root, with a .NET SDK supporting net8.0::

    dotnet restore .\tools\model-definition\ModelDefinition.csproj --locked-mode
    dotnet run --project .\tools\model-definition\ModelDefinition.csproj --no-restore -- validate
    dotnet run --project .\tools\model-definition\ModelDefinition.csproj --no-restore -- test
    dotnet run --project .\tools\model-definition\ModelDefinition.csproj --no-restore -- package

If this machine's NuGet v3 connection fails TLS negotiation, the official
NuGet v2 feed worked without disabling certificate validation::

    dotnet restore .\tools\model-definition\ModelDefinition.csproj --locked-mode --source https://www.nuget.org/api/v2/

Only dependency restore needs network access. All three application commands
are offline. They contain no connection, authentication or deployment code.
Microsoft.AnalysisServices has transitive identity-library dependencies;
this tool does not instantiate or call those identity clients.

Each command accepts an optional project-root path after the command name.
Do not point it at the semantic-model subdirectory. The root must contain
model\fixtures.json and model\Synthetic.SemanticModel.

What is actually checked
------------------------

Microsoft.AnalysisServices 19.114.12 performs the TMDL syntax and metadata
deserialization. The complete model is serialized and deserialized again,
including role definitions, before comparison. This is the genuine TOM
parser, not an indentation parser. Invalid TMDL has a negative parser test.

Additional local checks freeze this experiment's complete part set, typed
columns, single-direction relationships, read-only roles, absence of members,
security expression text, and all inline fixture expressions. The 12 subject
cases use ordinary C# arithmetic and ordinal string equality as an expected
result oracle. They do NOT execute DAX. M expressions are compared to a
deterministic renderer from fixtures.json; they are NOT evaluated by Power
Query. TMDL's parser preserves DAX/M expression text without compiling it.

All read input bytes are snapshotted before parsing and packaging. Intermediate
TOM files live under ignored artifacts\model-definition\checks in a unique
directory and are deleted after the check, including on failure.

Approval package
----------------

The package command creates:

* artifacts\model-definition\packages\<approval-sha256>\createSemanticModel.json
* artifacts\model-definition\packages\<approval-sha256>\approval.manifest.json

The request is for the Fabric Create Semantic Model operation, not the
generic Create Item operation. It contains displayName, description and
definition {format: TMDL, parts: [...]}. Every part is InlineBase64, including
both security roles, all table files, relationships, model, database, and
definition.pbism. No .platform, workspace, tenant or subscription is included.
Nothing sends this body to Fabric.

The manifest binds exact source bytes, fixture bytes, the serialized request,
and the pinned upstream guidance commit. No timestamp or local machine path
enters the hash. The manifest documents the exact SHA256 recipe. Different
line endings change the hash intentionally. Output is content-addressed,
read-back verified, and never overwrites differing bytes. Preserve the
approval hash alongside any later separately approved deployment.

Local validation is NOT evidence that Fabric accepts or processes the model,
that an application identity can set the effective role, or that CUSTOMDATA
reaches an RLS-enforced query context. Those are explicit later live gates.
No cloud deployment approval is represented by this package.
