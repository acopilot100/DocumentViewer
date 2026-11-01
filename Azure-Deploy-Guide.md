
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

## 2) Function App settings

Use the values in `DocumentViewer.Functions/appsettings.azure.template.json`.

Set these app settings in the Azure Function App:
- `AzureWebJobsStorage`
- `BlobStorageConnectionString`
- `BlobContainerName=document-preview-cache`
- `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated`

Do not upload `local.settings.json` to Azure.

## 3) Deploy the Azure Function

Push the code from `DocumentViewer.Functions` to the Azure Function App.

The required files are:
- `DocumentViewer_Functions.csproj`
- `Program.cs`
- `DocumentResolverFunction.cs`
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

This project currently uses a demo token list in `DocumentResolverFunction.cs`.
For production, replace that with your SharePoint lookup logic and token validation.
