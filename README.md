# Adliance.AzureBlobSimulator

![Container Build](https://github.com/adliance/AzureBlobSimulator/actions/workflows/docker-build-push.yml/badge.svg)

Simple ASP.NET Core (Kestrel) server that uses a local directory and simulates an API
that is compatible (enough) with Azure Blob Storage API to read/write files and directories on this local directory.

This tool is not intended to provide a fully compatible emulator for Azure Storage (use Azurite or other tools 
for that), but it can be used to make certain "cloud native" applications (that require Azure Blobs)
work with local directories.

For now, containers support read operations only, while blobs support upload, download, delete, and list operations.

Please note that the Azure Storage Manager performs many operations behind the scenes. 
While some features are not yet supported, the tool fully supports the official Azure.Storage.Blobs SDK, 
and all unit tests rely on this SDK.

## Supported operations
- Get account properties
- List containers
- Get container properties
- List blobs in a container
- Get blob properties
- Download blob
- Upload blobs
- Support SAS-URLs for all supported operations

In addition, health check endpoints `/health` and `/health/<container_name>` are available to check if the service 
in general, and specific containers are available.

## Unsupported operations
The following operations are not supported (yet), but it is planned to support them at some point in the future.
- Create containers
- Delete containers
- Account SAS

Other operations may or may not be supported in the future.

Also, not supported (to keep it simple for now are):
- Blobs with hierarchical namespaces (e.g. `my-container/sub-container/blob.txt`)

For now, authentication is supported only via Shared Key. Make sure to include the account name in the Blob Endpoint URL in the following format:

`<protocol>://<domain or ip with port>/<account>`

## Using Azure Storage Explorer (and AzCopy)

When connecting via Azure Storage Explorer (or using AzCopy directly), use `127.0.0.1` instead of `localhost`,
e.g. `http://127.0.0.1:10000/devstoreaccount1`.

Storage Explorer performs uploads via AzCopy, which only recognizes the `/<account>/<container>/<blob>` URL format
when the host is a literal IP address. With `localhost`, it treats the account name as the container and the
actual container as part of the blob name, which results in requests like
`HEAD /devstoreaccount1/my-container%2Fmy-file.pdf` that fail.


## Available as a Docker image

`docker pull ghcr.io/adliance/azureblobsimulator:latest`

You can freely configure account keys as well as different containers (each one mapping to a different local path).

For example, use this docker-compose file:

```
services:
  Adliance.AzureBlobSimulator:
    image: ghcr.io/adliance/azureblobsimulator:latest
    ports:
      - "10000:80"
    volumes:
      -  ./storage/default:/storage       
      -  ./storage/additional1:/first-container    
      -  ./storage/additional2:/second-container       
    environment:
      - Storage__LocalPath=/storage
      - Storage__Accounts__0__Name=devstoreaccount1
      - Storage__Accounts__0__Key=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==
      - Storage__Containers__0__Name=my-first-special-container
      - Storage__Containers__0__LocalPath=/first-container
      - Storage__Containers__1__Name=my-second-special-container
      - Storage__Containers__1__LocalPath=/second-container
```
