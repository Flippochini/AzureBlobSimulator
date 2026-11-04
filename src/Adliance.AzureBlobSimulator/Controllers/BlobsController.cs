using Adliance.AzureBlobSimulator.Attributes;
using Adliance.AzureBlobSimulator.Models;
using Adliance.AzureBlobSimulator.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace Adliance.AzureBlobSimulator.Controllers;

[ApiController]
public class BlobsController(ContainerService containerService) : ControllerBase
{
    [HttpGet("/{container}")]
    public IActionResult HandleGetRequest([FromRoute, ContainerName] string container, [FromQuery] string? comp, [FromQuery] string? restype)
        => restype switch
        {
            "container" when comp == "list" => ListBlobs(container),
            "container" when comp == null => GetContainerProperties(container),
            _ => BadRequest("Unsupported operation on GET /<container>.")
        };

    private IActionResult ListBlobs(string container)
    {
        if (!containerService.DoesContainerExist(container))
        {
            return NotFound($"Container \"{container}\" not found.");
        }

        var contentTypeProvider = new FileExtensionContentTypeProvider();

        var blobs = containerService.GetBlobNames(container)
            .Select(blobName =>
            {
                var meta = containerService.GetBlobMetadata(container, blobName);

                return new Blob
                {
                    // we can suppress this warning because we know that the name and ContentType are not null
                    Name = meta.Name!,
                    Properties = new BlobProperties
                    {
                        LastModified = meta.LastModified.ToString("R"),
                        Created = meta.Created.ToString("R"),
                        Etag = containerService.GetBlobETag(container, blobName),
                        ContentLength = meta.ContentLength,
                        ContentType = meta.ContentType!,
                        BlobType = "BlockBlob"
                    }
                };
            });

        var response = new BlobEnumerationResults
        {
            ServiceEndpoint = $"{Request.Scheme}://{Request.Host}/",
            ContainerName = container,
            Blobs = new BlobsCollection(blobs.ToList())
        };

        Response.Headers["x-ms-version"] = Constants.MsVersion;

        return Content(XmlSerializationService.SerializeToXml(response), "application/xml");
    }

    private IActionResult GetContainerProperties(string container)
    {
        if (!containerService.DoesContainerExist(container))
        {
            return NotFound($"Container \"{container}\" not found.");
        }

        Response.Headers["x-ms-version"] = Constants.MsVersion;
        Response.Headers["x-ms-lease-status"] = "unlocked";
        Response.Headers["x-ms-lease-state"] = "available";
        Response.Headers["x-ms-has-immutability-policy"] = "false";
        Response.Headers["x-ms-has-legal-hold"] = "false";
        Response.Headers["x-ms-default-encryption-scope"] = "$account-encryption-key";
        Response.Headers["x-ms-deny-encryption-scope-override"] = "false";
        Response.Headers.ETag = containerService.GetContainerETag(container);

        var lastModifiedTicks = containerService
            .GetContainerFiles(container)
            .Select(f => new FileInfo(f).LastWriteTimeUtc.Ticks)
            .DefaultIfEmpty(DateTime.UtcNow.Ticks)
            .Max();

        Response.Headers.LastModified = new DateTime(lastModifiedTicks, DateTimeKind.Utc).ToString("R");

        return Ok();
    }
}
