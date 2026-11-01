$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$frontendPath = Join-Path $repoRoot 'Localfiles'
$functionPath = Join-Path $repoRoot 'DocumentViewer.Functions'
$deployRoot = Join-Path $repoRoot 'deploy'

New-Item -ItemType Directory -Force -Path $deployRoot | Out-Null

Write-Host 'Building frontend...'
npm --prefix $frontendPath install
npm --prefix $frontendPath run build

Write-Host 'Publishing Azure Function...'
$publishPath = Join-Path $deployRoot 'function-app'
if (Test-Path $publishPath) { Remove-Item -Recurse -Force $publishPath }

dotnet publish $functionPath\DocumentViewer_Functions.csproj -c Release -o $publishPath

Write-Host 'Deployment package prepared:'
Write-Host "Frontend build: $frontendPath\dist"
Write-Host "Function publish: $publishPath"
Write-Host ''
Write-Host 'Upload the frontend build output to your static hosting resource.'
Write-Host 'Upload the function publish output to the Azure Function App.'
