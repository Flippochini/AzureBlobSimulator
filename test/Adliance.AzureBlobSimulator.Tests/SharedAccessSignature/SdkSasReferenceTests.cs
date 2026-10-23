using System.Text;
using Adliance.AzureBlobSimulator.Tests.Controllers;
using Azure.Core.Pipeline;
using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Adliance.AzureBlobSimulator.Tests.SharedAccessSignature;

/// <summary>
/// Uses the SAS implementation of the Azure SDK (<see cref="BlobSasBuilder"/> / GenerateSasUri) as reference implementation:
/// The SDK signs the request, the simulator has to accept it. Unlike the tests using <see cref="SasHelper"/>, the signature
/// is not computed by the simulator itself, so a wrong string-to-sign in the simulator would be detected here.
/// </summary>
public class SdkSasReferenceTests(WebApplicationFactory<Program> factory) : ControllerTestBase(factory)
{
    private const string ContainerName = "sas-container";
    private const string Content = "Hello SAS!";

    [Theory]
    [InlineData("my-little-test-blob_1.pdf")]
    [InlineData("my-little,test-blob_1.pdf")]
    [InlineData("my-little, test-blob_1.pdf")]
    [InlineData("my-little+test-blob_1.pdf")]
    [InlineData("my-little%25test-blob_1.pdf")]
    public async Task Can_Get_Blob_With_Sdk_Generated_Blob_Sas(string blobName)
    {
        await CreateBlob(blobName);

        var sasUri = CreateSharedKeyContainerClient(StorageAccountKey)
            .GetBlobClient(blobName)
            .GenerateSasUri(new BlobSasBuilder(BlobSasPermissions.Read, DateTimeOffset.UtcNow.AddHours(1))
            {
                BlobContainerName = ContainerName,
                BlobName = blobName,
                Resource = "b"
            });

        var response = await new BlobClient(sasUri, CreateClientOptions()).DownloadContentAsync();
        Assert.Equal(Content, response.Value.Content.ToString());
    }

    [Theory]
    [InlineData("my-little-test-blob_1.pdf")]
    [InlineData("my-little,test-blob_1.pdf")]
    [InlineData("my-little, test-blob_1.pdf")]
    [InlineData("my-little+test-blob_1.pdf")]
    [InlineData("my-little%25test-blob_1.pdf")]
    public async Task Can_Get_Blob_With_Sdk_Generated_Container_Sas(string blobName)
    {
        await CreateBlob(blobName);

        var sasUri = CreateSharedKeyContainerClient(StorageAccountKey)
            .GenerateSasUri(new BlobSasBuilder(BlobContainerSasPermissions.Read, DateTimeOffset.UtcNow.AddHours(1))
            {
                BlobContainerName = ContainerName,
                Resource = "c"
            });

        var response = await new BlobContainerClient(sasUri, CreateClientOptions()).GetBlobClient(blobName).DownloadContentAsync();
        Assert.Equal(Content, response.Value.Content.ToString());
    }

    [Fact]
    public async Task Will_Get_403_with_Sdk_Generated_Blob_Sas_Signed_with_Wrong_Key()
    {
        const string blobName = "my-little-test-blob_1.pdf";
        await CreateBlob(blobName);
        var wrongKey = Convert.ToBase64String(Encoding.UTF8.GetBytes("this-is-not-the-account-key"));

        var sasUri = CreateSharedKeyContainerClient(wrongKey)
            .GetBlobClient(blobName)
            .GenerateSasUri(new BlobSasBuilder(BlobSasPermissions.Read, DateTimeOffset.UtcNow.AddHours(1))
            {
                BlobContainerName = ContainerName,
                BlobName = blobName,
                Resource = "b"
            });

        await CustomAssert.RequestError(403, () => new BlobClient(sasUri, CreateClientOptions()).DownloadContentAsync());
    }

    private async Task CreateBlob(string blobName)
    {
        var containerPath = Path.Combine(TestStoragePath, ContainerName);
        Directory.CreateDirectory(containerPath);
        await File.WriteAllTextAsync(Path.Combine(containerPath, blobName), Content);
    }

    private BlobContainerClient CreateSharedKeyContainerClient(string accountKey)
    {
        // The SDK treats the ports 10000-10012 as path-style endpoints (account name as first path segment), which is what the
        // SAS middleware expects. The test server ignores host and port, so the requests still reach the simulator.
        var containerUri = new Uri($"http://localhost:10000/{StorageAccountName}/{ContainerName}");
        return new BlobContainerClient(containerUri, new StorageSharedKeyCredential(StorageAccountName, accountKey), CreateClientOptions());
    }

    private BlobClientOptions CreateClientOptions() => new()
    {
        Transport = new HttpClientTransport(Factory.CreateClient())
    };
}
