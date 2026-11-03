using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DocumentViewer.Functions;

public sealed class DocumentResolverFunction
{
    private readonly ILogger<DocumentResolverFunction> _logger;
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    private readonly DocumentBlobStorage _blobStorage;

    private static readonly IReadOnlyDictionary<string, DocumentReference> DemoTokens =
        new Dictionary<string, DocumentReference>(StringComparer.OrdinalIgnoreCase)
        {
            ["demo-doc"] = new("Designer_AI_Tagging.pdf", "http://localhost:3000/Designer_AI_Tagging.pdf", string.Empty),
            ["demo-docx"] = new("HighLevelAgentScriptFlow.docx", "http://localhost:3000/HighLevelAgentScriptFlow.docx", string.Empty),
            ["demo-pptx"] = new("sample.pptx", "http://localhost:3000/sample.pptx", string.Empty),
            ["demo-pdf"] = new("Designer_AI_Tagging.pdf", "http://localhost:3000/Designer_AI_Tagging.pdf", string.Empty)
        };

    public DocumentResolverFunction(
        ILogger<DocumentResolverFunction> logger,
        IConfiguration configuration,
        HttpClient httpClient,
        DocumentBlobStorage blobStorage)
    {
        _logger = logger;
        _configuration = configuration;
        _httpClient = httpClient;
        _blobStorage = blobStorage;
    }

    [Function("GetDocumentUrl")]
    public Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "document")] HttpRequestData req)
        => ResolveDocumentAsync(req, inline: false);

    [Function("GetDocumentContent")]
    public Task<HttpResponseData> RunContent(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "document/content")] HttpRequestData req)
        => ResolveDocumentAsync(req, inline: true);

    private async Task<HttpResponseData> ResolveDocumentAsync(HttpRequestData req, bool inline)
    {
        var response = req.CreateResponse();
        var token = req.Query["token"];

        if (string.IsNullOrWhiteSpace(token))
        {
            response.StatusCode = HttpStatusCode.BadRequest;
            await response.WriteStringAsync("{\"status\":\"missing_token\"}");
            response.Headers.Add("Content-Type", "application/json");
            return response;
        }

        try
        {
            var documentReference = await ResolveDocumentReferenceAsync(token);

            if (documentReference is null)
            {
                response.StatusCode = HttpStatusCode.NotFound;
                await response.WriteStringAsync("{\"status\":\"token_not_found\"}");
                response.Headers.Add("Content-Type", "application/json");
                return response;
            }

            using var payloadStream = await DownloadDocumentStreamAsync(documentReference);
            if (inline)
            {
                var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline");
                disposition.SetHttpFileName(documentReference.FileName);

                await payloadStream.CopyToAsync(response.Body);
                response.StatusCode = HttpStatusCode.OK;
                response.Headers.Add("Content-Type", GetContentType(documentReference.FileName));
                response.Headers.Add("Content-Disposition", disposition.ToString());
                return response;
            }

            var publicDocumentUrl = await _blobStorage.UploadAsync(documentReference.FileName, payloadStream);

            var payload = new
            {
                status = "ok",
                token,
                fileName = documentReference.FileName,
                uri = publicDocumentUrl,
                source = string.Equals(documentReference.SourceUrl, publicDocumentUrl, StringComparison.OrdinalIgnoreCase) ? "direct" : "temporary"
            };

            response.StatusCode = HttpStatusCode.OK;
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(JsonSerializer.Serialize(payload));
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Document resolution failed for token {Token}", token);
            response.StatusCode = HttpStatusCode.InternalServerError;
            await response.WriteStringAsync("{\"status\":\"document_unavailable\"}");
            response.Headers.Add("Content-Type", "application/json");
            return response;
        }
    }

    internal static string GetContentType(string fileName)
    {
        return Path.GetExtension(fileName.ToLowerInvariant()) switch
        {
            ".pdf" => "application/pdf",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".html" => "text/html",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream"
        };
    }

    private async Task<DocumentReference?> ResolveDocumentReferenceAsync(string token)
    {
        if (IsSharePointConfigured())
        {
            var sharePointDocument = await ResolveDocumentFromSharePointAsync(token);
            if (sharePointDocument is not null)
            {
                return sharePointDocument;
            }
        }

        if (DemoTokens.TryGetValue(token, out var demoDocument))
        {
            return demoDocument;
        }

        return null;
    }

    private async Task<DocumentReference?> ResolveDocumentFromSharePointAsync(string token)
    {
        try
        {
            var tenantId = _configuration["SharePointTenantId"];
            var clientId = _configuration["SharePointClientId"];
            var clientSecret = _configuration["SharePointClientSecret"];
            var siteId = _configuration["SharePointSiteId"];
            var listId = _configuration["SharePointListId"];

            if (string.IsNullOrWhiteSpace(tenantId)
                || string.IsNullOrWhiteSpace(clientId)
                || string.IsNullOrWhiteSpace(clientSecret)
                || string.IsNullOrWhiteSpace(siteId)
                || string.IsNullOrWhiteSpace(listId))
            {
                return null;
            }

            var tokenCredential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var accessToken = await tokenCredential.GetTokenAsync(new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }));

            var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items?$expand=fields&$filter=fields/AccessToke eq '{EscapeODataFilterValue(token)}'");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

            using var listResponse = await _httpClient.SendAsync(request);
            if (!listResponse.IsSuccessStatusCode)
            {
                var errorText = await listResponse.Content.ReadAsStringAsync();
                _logger.LogWarning("SharePoint token lookup failed: {StatusCode} {Reason} {Response}", listResponse.StatusCode, listResponse.ReasonPhrase, errorText);
                return null;
            }

            var listPayload = await listResponse.Content.ReadAsStringAsync();
            using var jsonDocument = JsonDocument.Parse(listPayload);
            if (!jsonDocument.RootElement.TryGetProperty("value", out var valueArray) || valueArray.ValueKind != JsonValueKind.Array || valueArray.GetArrayLength() == 0)
            {
                return null;
            }

            var item = valueArray[0];
            if (!item.TryGetProperty("fields", out var fields))
            {
                return null;
            }

            var driveId = GetJsonString(fields, "DriveID");
            var fileName = GetJsonString(fields, "FileLeafRef");
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = GetJsonString(fields, "FileName");
            }

            var path = GetJsonString(fields, "FileRef");
            if (string.IsNullOrWhiteSpace(path))
            {
                path = GetJsonString(fields, "Path");
            }

            _logger.LogInformation(
                "SharePoint list item resolved: token={Token}, driveId={DriveId}, fileName={FileName}, fileRef={FileRef}",
                token,
                driveId,
                fileName,
                path);

            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(driveId))
            {
                return null;
            }

            return new DocumentReference(fileName, path, driveId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SharePoint document lookup failed for token {Token}", token);
            return null;
        }
    }

    private async Task<Stream> DownloadDocumentStreamAsync(DocumentReference documentReference)
    {
        if (IsSharePointConfigured())
        {
            try
            {
                var tenantId = _configuration["SharePointTenantId"];
                var clientId = _configuration["SharePointClientId"];
                var clientSecret = _configuration["SharePointClientSecret"];
                var siteId = _configuration["SharePointSiteId"];

                if (!string.IsNullOrWhiteSpace(tenantId)
                    && !string.IsNullOrWhiteSpace(clientId)
                    && !string.IsNullOrWhiteSpace(clientSecret)
                    && !string.IsNullOrWhiteSpace(siteId))
                {
                    var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                    var accessToken = await credential.GetTokenAsync(new TokenRequestContext(new[] { "https://graph.microsoft.com/.default" }));

                    if (!string.IsNullOrWhiteSpace(documentReference.DriveId))
                    {
                        var relativePath = GetDriveRelativePath(documentReference.SourceUrl);
                        var driveUrl = $"https://graph.microsoft.com/v1.0/drives/{documentReference.DriveId}/root:/{relativePath}:/content";

                        _logger.LogInformation(
                            "SharePoint drive download: driveId={DriveId}, sourceUrl={SourceUrl}, relativePath={RelativePath}, requestUrl={RequestUrl}",
                            documentReference.DriveId,
                            documentReference.SourceUrl,
                            relativePath,
                            driveUrl);

                        var request = new HttpRequestMessage(HttpMethod.Get, driveUrl);
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

                        using var response = await _httpClient.SendAsync(request);
                        if (response.IsSuccessStatusCode)
                        {
                            var bytes = await response.Content.ReadAsByteArrayAsync();
                            return new MemoryStream(bytes);
                        }

                        var errorText = await response.Content.ReadAsStringAsync();
                        _logger.LogWarning("SharePoint drive download failed: {StatusCode} {Reason} {Response}", response.StatusCode, response.ReasonPhrase, errorText);
                    }

                    var relativeSitePath = NormalizeSharePointRelativePath(documentReference.SourceUrl);
                    var siteRequest = new HttpRequestMessage(
                        HttpMethod.Get,
                        $"https://graph.microsoft.com/v1.0/sites/{siteId}:{relativeSitePath}:/content");
                    siteRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);

                    using var siteResponse = await _httpClient.SendAsync(siteRequest);
                    if (siteResponse.IsSuccessStatusCode)
                    {
                        var bytes = await siteResponse.Content.ReadAsByteArrayAsync();
                        return new MemoryStream(bytes);
                    }

                    var siteErrorText = await siteResponse.Content.ReadAsStringAsync();
                    _logger.LogWarning("SharePoint file download failed: {StatusCode} {Reason} {Response}", siteResponse.StatusCode, siteResponse.ReasonPhrase, siteErrorText);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SharePoint file download failed for {FileName}", documentReference.FileName);
            }
        }

        if (!string.IsNullOrWhiteSpace(documentReference.SourceUrl))
        {
            using var sourceResponse = await _httpClient.GetAsync(documentReference.SourceUrl, HttpCompletionOption.ResponseHeadersRead);
            sourceResponse.EnsureSuccessStatusCode();
            return new MemoryStream(await sourceResponse.Content.ReadAsByteArrayAsync());
        }

        throw new InvalidOperationException($"No downloadable source available for {documentReference.FileName}");
    }

    private bool IsSharePointConfigured()
    {
        string[] settings =
        [
            "SharePointTenantId", "SharePointClientId", "SharePointClientSecret",
            "SharePointSiteId", "SharePointListId"
        ];
        if (!settings.Any(setting => !string.IsNullOrWhiteSpace(_configuration[setting])))
        {
            return false;
        }

        foreach (var setting in settings)
        {
            var value = _configuration[setting];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"SharePoint is partially configured: {setting} is required.");
            }

            DocumentBlobStorage.EnsureSecretResolved(value, setting);
        }

        return true;
    }

    private static string EscapeODataFilterValue(string value)
    {
        return value.Replace("'", "''");
    }

    private static string GetJsonString(JsonElement parent, string propertyName)
    {
        if (parent.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string NormalizeSharePointRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var sanitized = path.Trim();
        if (sanitized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || sanitized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(sanitized);
            sanitized = uri.AbsolutePath;
        }

        sanitized = sanitized.Replace('\\', '/');
        var segments = sanitized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.EscapeDataString);

        var encoded = string.Join("/", segments);
        return string.IsNullOrEmpty(encoded) ? string.Empty : "/" + encoded;
    }

    private static string GetDriveRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var sanitized = path.Trim();
        if (sanitized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || sanitized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(sanitized);
            sanitized = uri.AbsolutePath;
        }

        sanitized = sanitized.Replace('\\', '/');
        var segments = sanitized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();

        if (segments.Length == 0)
        {
            return string.Empty;
        }

        if (segments[0].Equals("sites", StringComparison.OrdinalIgnoreCase))
        {
            segments = segments.Skip(2).ToArray();
        }

        if (segments.Length > 0)
        {
            segments = segments.Skip(1).ToArray();
        }

        return string.Join("/", segments.Select(Uri.EscapeDataString));
    }

    private sealed record DocumentReference(string FileName, string SourceUrl, string DriveId);
}
