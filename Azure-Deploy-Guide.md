
# Azure deployment guide for the document viewer

This project contains:
- React SPA in `Localfiles`
- Azure Function in `DocumentViewer.Functions`

## 1) Create Azure resources in the portal

Create these resources in Azure:

1. Resource Group
2. Storage Account (general purpose v2)
3. Azure Function App
   - Runtime: .NET 8 isolated
   - Hosting plan: Consumption or Flex Consumption
   - Region: same region as the Storage account
4. Static Web App or Azure Storage static website for the frontend
5. Azure Key Vault for secrets that cannot be replaced by managed identity

## 2) Function App settings

Use `DocumentViewer.Functions/appsettings.azure.template.json` as a reference.
Its `Values` wrapper uses the local-settings format: in Azure, create each entry
inside `Values` as an individual Function App environment variable, not as a
nested JSON setting. This file is not loaded by the application.

### Recommended: managed identity for storage

Enable the Function App's **system-assigned managed identity** under Identity.
Set these app settings:

- `AzureWebJobsStorage__accountName=<host-storage-account-name>`
- `AzureWebJobsStorage__credential=managedidentity`
- `BlobServiceUri=https://<preview-storage-account-name>.blob.core.windows.net`
- `BlobContainerName=document-preview-cache`
- `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`

Remove `AzureWebJobsStorage` and `BlobStorageConnectionString` when using this
configuration. An exact `AzureWebJobsStorage` connection string takes precedence
over the identity-based settings. `BlobServiceUri`, when set, takes precedence
over `BlobStorageConnectionString`.

Grant the Function App identity:

| Purpose | Role | Scope |
| --- | --- | --- |
| Host storage for this HTTP-only Function | Storage Blob Data Owner | Host storage account |
| Upload preview blobs and create user delegation SAS URLs | Storage Blob Data Contributor | Preview storage account |

Use **account-level** scope for preview storage: requesting a user delegation key
is an account operation; container-only access is insufficient. No account key
is needed. The function issues a read-only, HTTPS-only SAS valid for 15 minutes.
Keep the preview container private and allow any required CORS origins.

The host and preview accounts may be the same account. If adding other triggers
or extensions, review their additional host storage roles (for example, Storage
Queue Data Contributor and Storage Account Contributor for blob triggers).
For sovereign clouds or custom DNS, use the explicit host settings
`AzureWebJobsStorage__blobServiceUri`, `AzureWebJobsStorage__queueServiceUri`,
and `AzureWebJobsStorage__tableServiceUri` instead of `__accountName`.

For a user-assigned identity, attach it to the Function App and set
`BlobManagedIdentityClientId=<identity-client-id>` for preview storage. Host
storage separately uses `AzureWebJobsStorage__clientId=<identity-client-id>`.
In Azure, the Blob client authenticates through managed identity; locally,
`DefaultAzureCredential` can use your Azure CLI/IDE login with the same RBAC roles.

### Key Vault references for SharePoint and unavoidable connection strings

The existing SharePoint integration uses an Entra application's client secret.
Store the **secret value**, not its secret ID, in Key Vault as
`sharepoint-client-secret`. Grant the Function App identity **Key Vault Secrets
User** on an RBAC-enabled vault. The deployment operator needs separate permission
to create/update secrets; the Function App only needs read access.

Configure these Function App settings:

```text
SharePointTenantId=<tenant-id>
SharePointClientId=<sharepoint-app-client-id>
SharePointClientSecret=@Microsoft.KeyVault(SecretUri=https://<vault-name>.vault.azure.net/secrets/sharepoint-client-secret)
SharePointSiteId=<site-id>
SharePointListId=<list-id>
```

Tenant, client, site, and list IDs are not secrets and can remain ordinary app
settings. Continue granting the SharePoint application's required Microsoft Graph
application permissions and admin consent; vault access does not grant Graph access.
If SharePoint is not used (local demo mode), remove or leave empty **all five**
SharePoint settings. Partial settings or unresolved references produce an error,
not a demo fallback.

App Service/Functions resolves Key Vault references before exposing settings to
the app, so no Key Vault SDK, vault credential, or custom secret-fetching code is
required. References use the system-assigned identity by default. To use a
user-assigned identity for references, set the Function App resource property
`keyVaultReferenceIdentity` to that identity's **resource ID**; setting
`BlobManagedIdentityClientId` does not change the vault identity.

If a hosting/deployment feature still requires a storage connection string, put
it in Key Vault and use a reference rather than a plaintext app setting:

```text
AzureWebJobsStorage=@Microsoft.KeyVault(SecretUri=https://<vault-name>.vault.azure.net/secrets/host-storage-connection-string)
BlobStorageConnectionString=@Microsoft.KeyVault(SecretUri=https://<vault-name>.vault.azure.net/secrets/preview-storage-connection-string)
```

For the preview connection-string option, remove `BlobServiceUri` to select it.
Use an account-key connection string, not a SAS-only connection string: the
compatibility path must sign new SAS URLs. Do not configure both host connection
models. Plan-specific deployment settings (such as
`WEBSITE_CONTENTAZUREFILECONNECTIONSTRING` on applicable Consumption/Premium
plans) are separate; preserve their required configuration and use Key Vault
references where the hosting feature supports them. Flex deployment storage
also has its own authentication configuration.

### Rotation, networking, and local development

- Use versionless secret URIs as above to pick up rotations. Azure caches
  references and normally refreshes them within 24 hours; a configuration change
  restarts the app and immediately refetches references. Keep old credentials
  valid until the replacement is confirmed.
- For a network-restricted vault or storage account, configure supported VNet
  integration, routing, firewall rules, and private DNS. RBAC alone is not enough.
- Confirm each reference reports **Resolved** in the Function App configuration.
  An unresolved reference remains literal text. The application rejects unresolved
  SharePoint/preview-storage secrets without logging their values; an unresolved
  host storage reference can prevent the Functions host from starting.
- `local.settings.json` is ignored by git. Local Functions Core Tools do **not**
  resolve Azure Key Vault reference strings. Use Azurite
  (`AzureWebJobsStorage=UseDevelopmentStorage=true`,
  `BlobStorageConnectionString=UseDevelopmentStorage=true`) or your local Azure
  login with `BlobServiceUri`. Supply any local SharePoint secret only through
  untracked settings or your development secret store.
- Do not upload `local.settings.json` to Azure, commit secret values, or put secrets
  in frontend `VITE_*` variables: Vite values are public browser code.

See Microsoft's guidance on [Key Vault references](https://learn.microsoft.com/azure/app-service/app-service-key-vault-references)
and [identity-based host storage](https://learn.microsoft.com/azure/azure-functions/functions-reference#connecting-to-host-storage-with-an-identity).

## 3) Deploy the Azure Function

Push the code from `DocumentViewer.Functions` to the Azure Function App.

The required files are:
- `DocumentViewer_Functions.csproj`
- `Program.cs`
- `DocumentResolverFunction.cs`
- `DocumentBlobStorage.cs`
- `host.json`
- generated build output from `dotnet publish`

## 4) Build the React frontend

From `Localfiles` run:

```bash
npm install
npm run build
```

This generates the production files under the build output folder (`dist`).

## 5) Deploy the frontend

Upload the generated frontend build output to:
- Static Web App, or
- Azure Storage static website, or
- App Service

## 6) Configure frontend environment values

Update `Localfiles/.env.production` with the real deployed values:

```text
VITE_FUNCTION_BASE_URL=https://<your-function-app-name>.azurewebsites.net/api
VITE_DOCUMENT_URL=https://<your-static-site-host>/Designer_AI_Tagging.pdf
VITE_FILE_NAME=Designer_AI_Tagging.pdf
VITE_BRAND_NAME=Document Portal
VITE_TERMS_URL=https://<your-company>/terms
```

## 7) Final runtime behavior

The end-to-end flow is:
- Browser loads the SPA
- SPA reads token from the URL
- SPA calls the Azure Function using `/api/document?token=...`
- Function validates the token
- Function copies the file to blob storage and returns a temporary SAS URL
- Browser loads that URL in `@cyntler/react-doc-viewer`

This works for PDF and public Office documents when the file is served via a public or SAS URL.

## 8) Production note

This project includes SharePoint lookup and a demo token list in
`DocumentResolverFunction.cs`. Key Vault protects configuration secrets; it does
not validate document-access tokens or remove the demo routes. Before production,
review token expiry, authorization, demo-token removal, and preview-blob retention.

Storage configuration, upload, or SAS-generation failures now produce an error
instead of returning a filename as if it were a working document URL.

## 9) Regression tests

With the .NET 8 SDK/runtime installed, run from the repository root:

```powershell
dotnet test .\DocumentViewer.Functions.Tests\DocumentViewer.Functions.Tests.csproj
```

Tests cover configuration precedence, unresolved Key Vault references, local
connection-string compatibility, read-only HTTPS SAS permissions and expiry,
storage failures, and SharePoint configuration errors. Azure identity permissions,
network reachability, and platform secret resolution must also be verified in
your deployed environment.
