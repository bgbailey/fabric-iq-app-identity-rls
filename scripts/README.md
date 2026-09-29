# Setup scripts

These scripts are intentionally small so the repository stays a clean technology how-to. They deploy only synthetic data and the identities needed for the ISV app identity + row-level-security pattern.

## Setup order

1. Create a Fabric workspace on a capacity.
2. Enable the needed tenant settings: service principals can use Fabric APIs; service principals can use Power BI APIs; Fabric IQ / Copilot is enabled for the metadata sign-in experience.
3. Deploy the semantic model:

   ```powershell
   pwsh .\scripts\Deploy-SemanticModel.ps1 -TenantId <tenant-guid> -WorkspaceId <workspace-guid>
   ```

4. Create the gateway app identity:

   ```powershell
   pwsh .\scripts\New-AppIdentity.ps1 -TenantId <tenant-guid>
   ```

5. Add the service principal as workspace Admin:

   ```powershell
   pwsh .\scripts\Add-WorkspaceMember.ps1 -TenantId <tenant-guid> -WorkspaceId <workspace-guid> -PrincipalId <service-principal-object-id> -PrincipalType ServicePrincipal -Role Admin
   ```

6. Create the public client used for the one-time Fabric IQ metadata sign-in:

   ```powershell
   pwsh .\scripts\New-MetadataClient.ps1 -TenantId <tenant-guid>
   ```

7. Optional: deploy the Embedded comparison report and copy the printed id into `Fabric:ReportId`.

   ```powershell
   pwsh .\scripts\Deploy-Report.ps1 -TenantId <tenant-guid> -WorkspaceId <workspace-guid> -SemanticModelId <semantic-model-guid>
   ```

8. Create Azure OpenAI with Bicep, or use an existing deployment. Grant the developer identity access while running locally:

   ```powershell
   az deployment group create -g <resource-group> -f .\scripts\infra\azure-openai.bicep -p accountName=<openai-account-name> openAiUserPrincipalId=<gateway-principal-object-id>
   az role assignment create --assignee <developer-object-id> --role "Cognitive Services OpenAI User" --scope <openai-account-resource-id>
   ```

9. Fill `src/SemanticGateway/appsettings.Local.json` (not committed) with your IDs. It overrides the placeholders in `appsettings.json`:

   ```json
   {
     "Fabric": {
       "TenantId": "<tenant-guid>",
       "WorkspaceId": "<workspace-guid>",
       "SemanticModelId": "<semantic-model-guid>",
       "ReportId": "<report-guid-or-null>",
       "ServicePrincipal": { "ClientId": "<app-client-guid>", "CertificateThumbprint": "<thumbprint>" }
     },
     "FabricIq": { "ClientId": "<metadata-client-guid>", "LoginHint": "<metadata-account-upn>" },
     "AzureOpenAI": { "Endpoint": "https://<resource>.openai.azure.com/", "Deployment": "<deployment-name>" }
   }
   ```

10. Build the portal into the gateway's `wwwroot`:

    ```powershell
    npm --prefix .\src\web install
    npm --prefix .\src\web run build
    ```

11. Sign in the metadata account once:

    ```powershell
    dotnet run --project .\src\SemanticGateway -- sign-in
    ```

12. Run the gateway and open http://localhost:5187:

    ```powershell
    dotnet run --project .\src\SemanticGateway
    ```

The service principal is workspace Admin because `executeDaxQueries` only allows the caller to specify an RLS role when the caller is Admin. The gateway still always sends the fixed role and `customData`, so model RLS remains the enforcement point.
