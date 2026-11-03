using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;

namespace DocumentViewer.Functions;

public sealed class DocumentBlobStorage(BlobServiceClient serviceClient, IConfiguration configuration)
{
    public static BlobServiceClient CreateClient(IConfiguration configuration)
    {
        var serviceUri = configuration["BlobServiceUri"];
        if (!string.IsNullOrWhiteSpace(serviceUri))
        {
            if (!Uri.TryCreate(serviceUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidOperationException("BlobServiceUri must be an absolute HTTPS storage endpoint.");
            }

            var clientId = configuration["BlobManagedIdentityClientId"];
            return new BlobServiceClient(uri, new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = string.IsNullOrWhiteSpace(clientId) ? null : clientId
            }));
        }

        var connectionString = configuration["BlobStorageConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Configure BlobServiceUri (recommended) or BlobStorageConnectionString.");
        }

        EnsureSecretResolved(connectionString, "BlobStorageConnectionString");
        return new BlobServiceClient(connectionString);
    }

    internal static void EnsureSecretResolved(string value, string settingName)
    {
        if (value.TrimStart().StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The Key Vault reference for {settingName} has not resolved. Check the Function App identity, vault permissions, and networking.");
        }
    }

    public async Task<string> UploadAsync(string fileName, Stream documentStream)
    {
        var containerName = configuration["BlobContainerName"];
        if (string.IsNullOrWhiteSpace(containerName))
        {
            throw new InvalidOperationException("BlobContainerName must be configured.");
        }

        var containerClient = serviceClient.GetBlobContainerClient(containerName);
        await containerClient.CreateIfNotExistsAsync();

        var safeName = Path.GetFileName(fileName);
        var blobName = string.IsNullOrWhiteSpace(safeName)
            ? $"document-{Guid.NewGuid():N}.bin"
            : safeName.Replace(' ', '-');
        var blobClient = containerClient.GetBlobClient(blobName);

        documentStream.Position = 0;
        await blobClient.UploadAsync(documentStream, overwrite: true);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b",
            StartsOn = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(15),
            Protocol = SasProtocol.Https
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        if (blobClient.CanGenerateSasUri)
        {
            return blobClient.GenerateSasUri(sasBuilder).ToString();
        }

        var delegationKey = await serviceClient.GetUserDelegationKeyAsync(sasBuilder.StartsOn, sasBuilder.ExpiresOn);
        var sasUri = new BlobUriBuilder(blobClient.Uri)
        {
            Sas = sasBuilder.ToSasQueryParameters(delegationKey.Value, serviceClient.AccountName)
        };
        return sasUri.ToUri().ToString();
    }
}
