using Azure;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace DocumentViewer.Functions.Tests;

public sealed class DocumentBlobStorageTests
{
    [Fact]
    public void IdentityEndpointTakesPrecedenceOverConnectionString()
    {
        var client = DocumentBlobStorage.CreateClient(Configuration(
            ("BlobServiceUri", "https://preview.blob.core.windows.net"),
            ("BlobStorageConnectionString", "@Microsoft.KeyVault(SecretUri=unresolved)")));

        Assert.Equal("https://preview.blob.core.windows.net/", client.Uri.AbsoluteUri);
    }

    [Fact]
    public void LocalEmulatorConnectionStringRemainsSupported()
    {
        var client = DocumentBlobStorage.CreateClient(Configuration(
            ("BlobStorageConnectionString", "UseDevelopmentStorage=true")));

        Assert.Equal("devstoreaccount1", client.AccountName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("@Microsoft.KeyVault(SecretUri=https://vault.vault.azure.net/secrets/storage)")]
    [InlineData(" @microsoft.keyvault(SecretUri=unresolved)")]
    public void MissingOrUnresolvedConnectionStringFailsExplicitly(string? value)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            DocumentBlobStorage.CreateClient(Configuration(("BlobStorageConnectionString", value))));

        Assert.DoesNotContain("SecretUri=", error.Message);
    }

    [Theory]
    [InlineData("http://preview.blob.core.windows.net")]
    [InlineData("not-an-endpoint")]
    public void InvalidIdentityEndpointIsRejected(string uri)
    {
        Assert.Throws<InvalidOperationException>(() =>
            DocumentBlobStorage.CreateClient(Configuration(("BlobServiceUri", uri))));
    }

    [Fact]
    public async Task MissingContainerFailsBeforeStorageCall()
    {
        var client = new Mock<BlobServiceClient>(MockBehavior.Strict);
        var storage = new DocumentBlobStorage(client.Object, Configuration());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            storage.UploadAsync("sample.pdf", new MemoryStream([1, 2, 3])));
        client.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UploadReturnsReadOnlyHttpsSasWithFifteenMinuteExpiry(bool sharedKey)
    {
        var service = new Mock<BlobServiceClient>();
        service.SetupGet(client => client.Uri).Returns(new Uri("https://preview.blob.core.windows.net"));
        var container = new Mock<BlobContainerClient>();
        var blob = new Mock<BlobClient>();
        var uri = new Uri("https://preview.blob.core.windows.net/cache/sample-file.pdf");
        var before = DateTimeOffset.UtcNow;
        using var content = new MemoryStream([1, 2, 3]);
        content.Position = 2;

        service.Setup(client => client.GetBlobContainerClient("cache")).Returns(container.Object);
        container.Setup(client => client.GetBlobClient("sample-file.pdf")).Returns(blob.Object);
        blob.SetupGet(client => client.Uri).Returns(uri);
        blob.SetupGet(client => client.CanGenerateSasUri).Returns(sharedKey);
        blob.Setup(client => client.UploadAsync(content, true, It.IsAny<CancellationToken>()))
            .Callback(() => Assert.Equal(0, content.Position))
            .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobContentInfo(
                new ETag("test"), before, [], "test", 0), Mock.Of<Response>()));

        BlobSasBuilder? signedSas = null;
        if (sharedKey)
        {
            blob.Setup(client => client.GenerateSasUri(It.IsAny<BlobSasBuilder>()))
                .Returns((BlobSasBuilder sas) =>
                {
                    signedSas = sas;
                    return new BlobUriBuilder(uri)
                    {
                        Sas = sas.ToSasQueryParameters(new StorageSharedKeyCredential(
                            "preview", Convert.ToBase64String(new byte[32])))
                    }.ToUri();
                });
        }
        else
        {
            service.Setup(client => client.GetUserDelegationKeyAsync(
                    It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .Returns((DateTimeOffset? start, DateTimeOffset end, CancellationToken _) =>
                {
                    Assert.NotNull(start);
                    Assert.InRange(start.Value, before.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(-1));
                    Assert.InRange(end, before.AddMinutes(15), DateTimeOffset.UtcNow.AddMinutes(15));
                    var key = BlobsModelFactory.UserDelegationKey(
                        "object-id", "tenant-id", start.Value, end, "b", "2023-11-03",
                        Convert.ToBase64String(new byte[32]));
                    return Task.FromResult(Response.FromValue(key, Mock.Of<Response>()));
                });
        }

        var storage = new DocumentBlobStorage(service.Object, Configuration(("BlobContainerName", "cache")));
        var result = new Uri(await storage.UploadAsync("sample file.pdf", content));
        var query = System.Web.HttpUtility.ParseQueryString(result.Query);

        Assert.Equal(uri.GetLeftPart(UriPartial.Path), result.GetLeftPart(UriPartial.Path));
        Assert.Equal("r", query["sp"]);
        Assert.Equal("https", query["spr"]);
        Assert.Equal("b", query["sr"]);
        Assert.False(string.IsNullOrWhiteSpace(query["sig"]));
        // SAS timestamps are serialized to whole seconds.
        Assert.InRange(DateTimeOffset.Parse(query["se"]!), before.AddMinutes(15).AddSeconds(-1),
            DateTimeOffset.UtcNow.AddMinutes(15));
        Assert.InRange(DateTimeOffset.Parse(query["st"]!), before.AddMinutes(-1).AddSeconds(-1),
            DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(sharedKey, signedSas is not null);
        blob.Verify(client => client.UploadAsync(content, true, It.IsAny<CancellationToken>()), Times.Once);
        service.Verify(client => client.GetUserDelegationKeyAsync(
            It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            sharedKey ? Times.Never() : Times.Once());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageFailureDoesNotReturnFilename(bool failDelegationKey)
    {
        var service = new Mock<BlobServiceClient>();
        var container = new Mock<BlobContainerClient>();
        var blob = new Mock<BlobClient>();
        service.Setup(client => client.GetBlobContainerClient("cache")).Returns(container.Object);
        container.Setup(client => client.GetBlobClient("sample.pdf")).Returns(blob.Object);
        if (failDelegationKey)
        {
            service.Setup(client => client.GetUserDelegationKeyAsync(
                    It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(403, "Access denied"));
        }
        else
        {
            blob.Setup(client => client.UploadAsync(It.IsAny<Stream>(), true, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new RequestFailedException(403, "Access denied"));
        }
        var storage = new DocumentBlobStorage(service.Object, Configuration(("BlobContainerName", "cache")));

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            storage.UploadAsync("sample.pdf", new MemoryStream([1])));
    }

    internal static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value))).Build();
}
