# React Document Viewer SPA

This is a starter Single Page Application for anonymously accessible document viewing in React, built around the `@cyntler/react-doc-viewer` package.

## Purpose

The app is designed for:
- PDF previews
- Office document previews such as DOCX, PPTX, and XLSX
- MP4/video previews
- a standard page header and footer
- a download button in the header
- a terms and conditions link in the footer
- future integration with a SharePoint token flow and Azure Function backend

## Recommended architecture

For production, use:
1. A React SPA in Azure Static Web Apps or App Service
2. A separate Azure Function to resolve the SharePoint file using a token and return a secured public URL or stream
3. A SharePoint list lookup to resolve the final document reference before retrieval

## Local Office file examples

The app serves these sample files from the local public folder:

- `/Designer_AI_Tagging.pdf`
- `/HighLevelAgentScriptFlow.docx`
- `/sample.docx`
- `/sample.pptx`

This demonstrates the viewer can load local PDF and Office document examples directly from the project.

## Local development

From the project folder:

```bash
npm install
npm run dev -- --host 0.0.0.0 --port 3000
```

Open:

```text
http://localhost:3000/
```

## Environment variables

Copy `.env.example` to `.env` and adjust values as required:

```env
VITE_DOCUMENT_URL=/Designer_AI_Tagging.pdf
VITE_FILE_NAME=Designer_AI_Tagging.pdf
VITE_BRAND_NAME=Document Portal
VITE_TERMS_URL=https://example.com/terms
```

If you later want to load a SharePoint document, point `VITE_DOCUMENT_URL` at the public URL returned by your Azure Function or a public SharePoint file URL.

## Notes on the library

The README for `@cyntler/react-doc-viewer` confirms the library supports:
- PDF
- Office documents such as DOC/DOCX/PPT/PPTX/XLS/XLSX
- PNG/JPG/GIF and other browser-supported files
- MP4 video

Important caveat: the library expects a public URL for Office documents. In production, this means your Azure Function or SharePoint URL must be browser-accessible and compatible with the document preview service.

## Suggested Azure hosting pattern

- Frontend: Azure Static Web Apps or Azure App Service
- Backend: Azure Functions
- Security: token is expected in the URL or Authorization header
- Data source: SharePoint list lookup and SharePoint library retrieval

## Example SharePoint flow

1. Receive visitor token from SPA URL
2. Call Azure Function with the token
3. Function resolves the document record from SharePoint list
4. Function returns a file URL or a temporary authenticated document stream
5. React viewer loads the final document into the browser

This starter keeps the UI and the document viewer flow simple while leaving the SharePoint and Azure Function integration to the next step.
