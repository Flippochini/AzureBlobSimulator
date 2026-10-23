using System.Text;
using Adliance.AzureBlobSimulator.Controllers;
using Adliance.AzureBlobSimulator.Models;
using Adliance.AzureBlobSimulator.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Adliance.AzureBlobSimulator.Tests.Controllers;

public class BlobControllerTests(WebApplicationFactory<Program> factory) : ControllerTestBase(factory)
{
    [Fact]
    public async Task Can_Delete_Blob()
    {
        const string containerName = "upload-test-container";
        const string blobName = "uploaded-file.txt";
        var containerPath = Path.Combine(TestStoragePath, containerName);
        await Can_Upload_Blob();
        Assert.True(File.Exists(Path.Combine(containerPath, blobName)));

        var containerClient = BlobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);
        await blobClient.DeleteAsync();
        Assert.False(File.Exists(Path.Combine(containerPath, blobName)));
    }

    [Fact]
    public async Task Can_Upload_Blob()
    {
        const string containerName = "upload-test-container";
        const string blobName = "uploaded-file.txt";
        var containerPath = Path.Combine(TestStoragePath, containerName);
        Directory.CreateDirectory(containerPath);

        var containerClient = BlobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        var content = BinaryData.FromString("Hello Upload!");
        await blobClient.UploadAsync(content);

        var download = await blobClient.DownloadContentAsync();
        Assert.Equal("Hello Upload!", download.Value.Content.ToString());
        Assert.True(File.Exists(Path.Combine(containerPath, blobName)));
    }

    [Fact]
    public async Task Upload_To_NonExisting_Container_Should_Return_404()
    {
        const string containerName = "nonexisting-container";
        var blobClient = BlobServiceClient.GetBlobContainerClient(containerName).GetBlobClient("file.txt");
        await CustomAssert.RequestError(404, () => blobClient.UploadAsync(BinaryData.FromString("x")));
    }

    [Theory]
    [InlineData("my-little-test-blob_1.pdf")]
    [InlineData("my-little,test-blob_1.pdf")]
    [InlineData("my-little, test-blob_1.pdf")]
    [InlineData("my-little+test-blob_1.pdf")]
    [InlineData("my-little%25test-blob_1.pdf")]
    public async Task Can_Get_Blob(string blobName)
    {
        const string containerName = "test-container";
        var containerPath = Path.Combine(TestStoragePath, containerName);
        Directory.CreateDirectory(containerPath);
        await File.WriteAllBytesAsync(Path.Combine(containerPath, blobName), "Hello World!"u8.ToArray());

        var containerClient = BlobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);
        var response = await blobClient.DownloadContentAsync();
        Assert.NotNull(response?.Value);
        Assert.Equal("Hello World!", response.Value.Content.ToString());
    }

    [Fact]
    public async Task Can_Get_Properties_of_Blob()
    {
        const string containerName = "test-container";
        const string blobName = "my-little-test-blob_1.pdf";
        var containerPath = Path.Combine(TestStoragePath, containerName);
        Directory.CreateDirectory(containerPath);
        await File.WriteAllBytesAsync(Path.Combine(containerPath, blobName), "Hello World!"u8.ToArray());

        var containerClient = BlobServiceClient.GetBlobContainerClient(containerName);
        var blobClient = containerClient.GetBlobClient(blobName);
        var response = await blobClient.GetPropertiesAsync();
        Assert.NotNull(response?.Value);
        Assert.Equal("Hello World!".Length, response.Value.ContentLength);
    }

    [Fact]
    public async Task Will_Get_404_when_Getting__Blob_that_does_not_Exist()
    {
        var containerClient = BlobServiceClient.GetBlobContainerClient("test-container");
        var blobClient = containerClient.GetBlobClient("test-blob");
        await CustomAssert.RequestError(404, blobClient.DownloadContentAsync);
    }

    [Fact]
    public async Task Will_Get_404_when_Getting_Properties_of_Blob_that_does_not_Exist()
    {
        var containerClient = BlobServiceClient.GetBlobContainerClient("test-container");
        var blobClient = containerClient.GetBlobClient("test-blob");
        await CustomAssert.RequestError(404, () => blobClient.GetPropertiesAsync());
    }

    [Fact]
    public async Task Will_Get_400_when_Getting_Blob_in_Container_with_Invalid_Name()
    {
        var containerClient = BlobServiceClient.GetBlobContainerClient("_testcontainer");
        var blobClient = containerClient.GetBlobClient("test-blob");
        await CustomAssert.RequestError(400, () => blobClient.GetPropertiesAsync());
    }

    [Fact]
    public async Task Can_Upload_Blob_Into_Subdirectory()
    {
        const string containerName = "upload-test-container";
        const string blobName = "folder1/folder2/uploaded-file.txt";

        Directory.CreateDirectory(
            Path.Combine(TestStoragePath, containerName));

        var blobClient = BlobServiceClient
            .GetBlobContainerClient(containerName)
            .GetBlobClient(blobName);

        await blobClient.UploadAsync(
            BinaryData.FromString("Hello Subdirectory!"));

        var download = await blobClient.DownloadContentAsync();

        Assert.Equal(
            "Hello Subdirectory!",
            download.Value.Content.ToString());

        Assert.True(File.Exists(
            Path.Combine(
                TestStoragePath,
                containerName,
                "folder1",
                "folder2",
                "uploaded-file.txt")));
    }

    [Fact]
    public async Task Can_Get_Blob_From_Subdirectory()
    {
        const string containerName = "test-container";
        const string blobName = "folder1/folder2/test.txt";

        var filePath = Path.Combine(
            TestStoragePath,
            containerName,
            "folder1",
            "folder2",
            "test.txt");

        Directory.CreateDirectory(
            Path.GetDirectoryName(filePath)!);

        await File.WriteAllTextAsync(filePath, "Hello World!");

        var blobClient = BlobServiceClient
            .GetBlobContainerClient(containerName)
            .GetBlobClient(blobName);

        var response = await blobClient.DownloadContentAsync();

        Assert.Equal(
            "Hello World!",
            response.Value.Content.ToString());
    }

    [Fact]
    public async Task Can_Delete_Blob_From_Subdirectory()
    {
        const string containerName = "test-container";
        const string blobName = "folder1/folder2/test.txt";

        var filePath = Path.Combine(
            TestStoragePath,
            containerName,
            "folder1",
            "folder2",
            "test.txt");

        Directory.CreateDirectory(
            Path.GetDirectoryName(filePath)!);

        await File.WriteAllTextAsync(filePath, "Delete Me");

        var blobClient = BlobServiceClient
            .GetBlobContainerClient(containerName)
            .GetBlobClient(blobName);

        await blobClient.DeleteAsync();

        Assert.False(File.Exists(filePath));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("../../evil.txt")]
    [InlineData("folder/../evil.txt")]
    [InlineData(@"..\evil.txt")]
    [InlineData(@"folder\..\evil.txt")]
    public async Task WriteBlob_Should_Reject_Path_Traversal(string blobName)
    {
        const string containerName = "upload-test-container";
        var controller = CreateBlobController(containerName);

        var context = new DefaultHttpContext();
        context.Request.Headers["x-ms-blob-type"] = "BlockBlob";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("evil"));

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = context
        };

        try
        {
            await controller.WriteBlob(containerName, blobName);
        }
        catch (Exception ex)
        {
            var badRequest = Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains("Invalid blob segment.", badRequest.Message);
            return;
        }

        Assert.Fail("Should have thrown an InvalidOperationException with message containing 'Invalid blob segment.'");
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("../../evil.txt")]
    [InlineData("folder/../evil.txt")]
    [InlineData(@"..\evil.txt")]
    public void ReadBlob_Should_Reject_Path_Traversal(string blobName)
    {
        const string containerName = "test-container";
        var controller = CreateBlobController(containerName);

        try
        {
            controller.ReadBlob(containerName, blobName);
        }
        catch (Exception ex)
        {
            var badRequest = Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains("Invalid blob segment.", badRequest.Message);
            return;
        }

        Assert.Fail("Should have thrown InvalidOperationException with 'Invalid blob segment.'");
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("../../evil.txt")]
    [InlineData("folder/../evil.txt")]
    [InlineData(@"..\evil.txt")]
    public void GetProperties_Should_Reject_Path_Traversal(string blobName)
    {
        const string containerName = "test-container";
        var controller = CreateBlobController(containerName);

        try
        {
            controller.GetBlobProperties(containerName, blobName);
        }
        catch (Exception ex)
        {
            var badRequest = Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains("Invalid blob segment.", badRequest.Message);
            return;
        }

        Assert.Fail("Should have thrown InvalidOperationException with 'Invalid blob segment.'");
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("../../evil.txt")]
    [InlineData("folder/../evil.txt")]
    [InlineData(@"..\evil.txt")]
    public void Delete_Should_Reject_Path_Traversal(string blobName)
    {
        const string containerName = "test-container";
        var controller = CreateBlobController(containerName);

        try
        {
            controller.DeleteBlob(containerName, blobName);
        }
        catch (Exception ex)
        {
            var badRequest = Assert.IsType<InvalidOperationException>(ex);
            Assert.Contains("Invalid blob segment.", badRequest.Message);
            return;
        }

        Assert.Fail("Should have thrown InvalidOperationException with 'Invalid blob segment.'");
    }

    [Fact]
    public async Task Delete_Cannot_Delete_File_Outside_Container()
    {
        const string containerName = "test-container";

        var outsideFile = Path.Combine(
            TestStoragePath,
            "outside.txt");

        await File.WriteAllTextAsync(
            outsideFile,
            "secret");

        var blobClient = BlobServiceClient
            .GetBlobContainerClient(containerName)
            .GetBlobClient("../outside.txt");

        await CustomAssert.RequestError(
            400,
            () => blobClient.DeleteAsync());

        Assert.True(File.Exists(outsideFile));
    }

    private BlobController CreateBlobController(string containerName)
    {
        // We need to create the directory manually because container creation is not supported
        var containerPath = Path.Combine(TestStoragePath, containerName);
        Directory.CreateDirectory(containerPath);

        var containerService = new ContainerService(Options.Create(new StorageOptions
        {
            LocalPath = TestStoragePath,
            Containers =
            [
                new ContainerOptions
                {
                    Name = containerName,
                    LocalPath = containerPath
                }
            ]
        }));

        return new BlobController(containerService);
    }
}
