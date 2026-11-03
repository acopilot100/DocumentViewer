using System.Collections.Specialized;
using System.Net;
using System.Text;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DocumentViewer.Functions.Tests;

public sealed class DocumentResolverFunctionTests
{
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

    private static HttpRequestData CreateRequest(string token, Stream body)
    {
        var context = Mock.Of<FunctionContext>();
        var response = new Mock<HttpResponseData>(context);
        response.SetupProperty(value => value.StatusCode);
        response.SetupGet(value => value.Headers).Returns(new HttpHeadersCollection());
        response.SetupGet(value => value.Body).Returns(body);
        var request = new Mock<HttpRequestData>(context);
        request.SetupGet(value => value.Url).Returns(new Uri($"https://localhost/api/document?token={token}"));
        request.SetupGet(value => value.Query).Returns(new NameValueCollection { ["token"] = token });
        request.Setup(value => value.CreateResponse()).Returns(response.Object);
        return request.Object;
    }
}
