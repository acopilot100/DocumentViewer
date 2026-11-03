using System.Collections.Specialized;
using System.Net;
using System.Text;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using Xunit;

namespace DocumentViewer.Functions.Tests;

public sealed class DocumentResolverFunctionTests
{
    [Theory]
    [InlineData("Report.PDF", "application/pdf")]
    [InlineData("Photo.JPG", "image/jpeg")]
    [InlineData("Photo.JpEg", "image/jpeg")]
    [InlineData("Image.PNG", "image/png")]
    [InlineData("Page.HTML", "text/html")]
    [InlineData("Report.DOCX", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("Report.XLSX", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("Slides.PPTX", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [InlineData("archive.zip", "application/octet-stream")]
    [InlineData("README", "application/octet-stream")]
    [InlineData("report.pdf.txt", "application/octet-stream")]
    public void ContentTypeIsDeterminedByLowercaseExtension(string fileName, string expected)
    {
        Assert.Equal(expected, DocumentResolverFunction.GetContentType(fileName));
    }

    [Theory]
    [InlineData("demo-pdf", "Designer_AI_Tagging.pdf", "application/pdf")]
    [InlineData("demo-docx", "HighLevelAgentScriptFlow.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("demo-pptx", "sample.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    public async Task ContentEndpointReturnsOriginalBytesInlineWithoutBlobUpload(string token, string fileName, string contentType)
    {
        byte[] content = [0, 1, 127, 128, 255];
        var handler = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        handler.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.Is<HttpRequestMessage>(request => request.RequestUri == new Uri($"http://localhost:3000/{fileName}")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
        using var httpClient = new HttpClient(handler.Object);
        var configuration = DocumentBlobStorageTests.Configuration();
        var storage = new Mock<BlobServiceClient>(MockBehavior.Strict);
        var function = new DocumentResolverFunction(
            NullLogger<DocumentResolverFunction>.Instance, configuration, httpClient,
            new DocumentBlobStorage(storage.Object, configuration));
        using var body = new MemoryStream();

        var result = await function.RunContent(CreateRequest(token, body, response =>
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(contentType, Assert.Single(response.Headers.GetValues("Content-Type")));
            var header = System.Net.Http.Headers.ContentDispositionHeaderValue.Parse(
                Assert.Single(response.Headers.GetValues("Content-Disposition")));
            Assert.Equal("inline", header.DispositionType);
            Assert.Equal(fileName, header.FileNameStar);
        }));

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(content, body.ToArray());
        Assert.Equal(contentType, Assert.Single(result.Headers.GetValues("Content-Type")));
        var disposition = System.Net.Http.Headers.ContentDispositionHeaderValue.Parse(
            Assert.Single(result.Headers.GetValues("Content-Disposition")));
        Assert.Equal("inline", disposition.DispositionType);
        Assert.Equal(fileName, disposition.FileNameStar);
        storage.VerifyNoOtherCalls();
        handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Theory]
    [InlineData(null, HttpStatusCode.BadRequest, "missing_token")]
    [InlineData(" ", HttpStatusCode.BadRequest, "missing_token")]
    [InlineData("unknown", HttpStatusCode.NotFound, "token_not_found")]
    public async Task ContentEndpointPreservesTokenErrors(string? token, HttpStatusCode status, string error)
    {
        using var httpClient = new HttpClient();
        var configuration = DocumentBlobStorageTests.Configuration();
        var storage = new Mock<BlobServiceClient>(MockBehavior.Strict);
        var function = new DocumentResolverFunction(
            NullLogger<DocumentResolverFunction>.Instance, configuration, httpClient,
            new DocumentBlobStorage(storage.Object, configuration));
        using var body = new MemoryStream();

        var result = await function.RunContent(CreateRequest(token, body));

        Assert.Equal(status, result.StatusCode);
        Assert.Equal($"{{\"status\":\"{error}\"}}", Encoding.UTF8.GetString(body.ToArray()));
        Assert.Equal("application/json", Assert.Single(result.Headers.GetValues("Content-Type")));
        Assert.False(result.Headers.Contains("Content-Disposition"));
        storage.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ContentEndpointReturnsJsonErrorWhenDownloadFails()
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler.Object);
        var configuration = DocumentBlobStorageTests.Configuration();
        var storage = new Mock<BlobServiceClient>(MockBehavior.Strict);
        var function = new DocumentResolverFunction(
            NullLogger<DocumentResolverFunction>.Instance, configuration, httpClient,
            new DocumentBlobStorage(storage.Object, configuration));
        using var body = new MemoryStream();

        var result = await function.RunContent(CreateRequest("demo-pdf", body));

        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
        Assert.Equal("{\"status\":\"document_unavailable\"}", Encoding.UTF8.GetString(body.ToArray()));
        Assert.Equal("application/json", Assert.Single(result.Headers.GetValues("Content-Type")));
        Assert.False(result.Headers.Contains("Content-Disposition"));
        storage.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("SharePointTenantId", "tenant")]
    [InlineData("SharePointSiteId", "site")]
    [InlineData("SharePointClientSecret", "@Microsoft.KeyVault(SecretUri=unresolved)")]
    public async Task PartialSharePointConfigurationDoesNotFallBackToDemo(string key, string value)
    {
        await AssertConfigurationFailure((key, value));
    }

    [Fact]
    public async Task UnresolvedSharePointReferenceDoesNotFallBackToDemo()
    {
        await AssertConfigurationFailure(
            ("SharePointTenantId", "tenant"),
            ("SharePointClientId", "client"),
            ("SharePointClientSecret", "@Microsoft.KeyVault(SecretUri=unresolved)"),
            ("SharePointSiteId", "site"),
            ("SharePointListId", "list"));
    }

    [Fact]
    public async Task UnknownTokenStillReturnsNotFoundWhenSharePointIsDisabled()
    {
        using var httpClient = new HttpClient();
        var configuration = DocumentBlobStorageTests.Configuration();
        var function = new DocumentResolverFunction(
            NullLogger<DocumentResolverFunction>.Instance, configuration, httpClient,
            new DocumentBlobStorage(Mock.Of<BlobServiceClient>(), configuration));
        using var body = new MemoryStream();
        var request = CreateRequest("unknown", body);

        var result = await function.Run(request);

        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Equal("{\"status\":\"token_not_found\"}", Encoding.UTF8.GetString(body.ToArray()));
    }

    private static async Task AssertConfigurationFailure(params (string Key, string? Value)[] values)
    {
        using var httpClient = new HttpClient();
        var configuration = DocumentBlobStorageTests.Configuration(values);
        var storage = new Mock<BlobServiceClient>(MockBehavior.Strict);
        var function = new DocumentResolverFunction(
            NullLogger<DocumentResolverFunction>.Instance, configuration, httpClient,
            new DocumentBlobStorage(storage.Object, configuration));
        using var body = new MemoryStream();
        var request = CreateRequest("demo-doc", body);

        var result = await function.Run(request);

        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
        Assert.Equal("{\"status\":\"document_unavailable\"}", Encoding.UTF8.GetString(body.ToArray()));
        storage.VerifyNoOtherCalls();
    }

    private static HttpRequestData CreateRequest(
        string? token, Stream body, Action<HttpResponseData>? beforeBodyAccess = null)
    {
        var context = Mock.Of<FunctionContext>();
        var response = new Mock<HttpResponseData>(context);
        response.SetupProperty(value => value.StatusCode);
        response.SetupGet(value => value.Headers).Returns(new HttpHeadersCollection());
        response.SetupGet(value => value.Body).Returns(() =>
        {
            Assert.True(response.Object.Headers.Contains("Content-Type"),
                "Content-Type must be set before accessing the response body.");
            beforeBodyAccess?.Invoke(response.Object);
            return body;
        });
        var request = new Mock<HttpRequestData>(context);
        request.SetupGet(value => value.Url).Returns(new Uri($"https://localhost/api/document?token={token}"));
        request.SetupGet(value => value.Query).Returns(new NameValueCollection { ["token"] = token });
        request.Setup(value => value.CreateResponse()).Returns(response.Object);
        return request.Object;
    }
}
