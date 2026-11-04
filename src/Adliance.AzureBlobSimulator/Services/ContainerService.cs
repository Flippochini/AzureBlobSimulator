using Adliance.AzureBlobSimulator.Models;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace Adliance.AzureBlobSimulator.Services;

public class ContainerService(IOptions<StorageOptions> options)
{
    private Dictionary<string, string>? _containers;

    public Dictionary<string, string> GetContainers()
        => _containers ??= BuildContainers();

    public bool DoesContainerExist(string container)
    {
        return GetContainers().ContainsKey(container);
    }

    public IEnumerable<string> GetBlobNames(string container)
    {
        if (!GetContainers().TryGetValue(container, out var containerPath))
        {
            return new List<string>();
        }

        return GetFilesOrEmpty(containerPath)
            .Select(f => Path.GetRelativePath(containerPath, f).Replace("\\", "/"));
    }

    /// <summary>
    /// Returns all files of a container. System containers like $logs or $blobchangefeed
    /// are not necessarily backed by a directory on disk, in which case they are treated as empty.
    /// </summary>
    public string[] GetContainerFiles(string container)
        => GetFilesOrEmpty(GetContainerPath(container));

    private static string[] GetFilesOrEmpty(string directory)
        => Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            : [];

    public string GetContainerPath(string container)
    {
        if (GetContainers().TryGetValue(container, out var containerPath))
        {
            return Path.GetFullPath(containerPath);
        }

        throw new DirectoryNotFoundException($"Container \"{container}\" not found.");
    }

    public string GetBlobPath(string container, string blob)
    {
        if (!GetContainers().TryGetValue(container, out var containerPath))
        {
            throw new DirectoryNotFoundException($"Container \"{container}\" not found.");
        }

        return ResolveBlobPath(containerPath, blob);
    }

    public string GetBlobETag(string container, string blob)
    {
        var path = GetBlobPath(container, blob);
        var fileInfo = new FileInfo(path);

        return GetBlobETagByFileInfo(fileInfo);
    }

    public string GetContainerETag(string container)
    {
        var files = GetContainerFiles(container);

        if (files.Length == 0)
        {
            return "\"0-0\"";
        }

        var hash = files
            .Select(f => new FileInfo(f))
            .Aggregate(0L, (acc, fi) =>
                HashCode.Combine(acc, fi.Length, fi.LastWriteTimeUtc.Ticks));

        return $"\"{files.Length}-{hash}\"";
    }

    public BlobMetadata GetBlobMetadata(string container, string blob)
    {
        var filePath = GetBlobPath(container, blob);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Blob \"{container}/{blob}\" not found.");
        }

        var fileInfo = new FileInfo(filePath);

        var contentTypeProvider = new FileExtensionContentTypeProvider();
        var contentType =
            contentTypeProvider.TryGetContentType(fileInfo.Name, out var ct)
                ? ct
                : "application/octet-stream";

        return new BlobMetadata
        {
            Path = filePath,
            Name = blob,

            ContentLength = fileInfo.Length,
            ContentType = contentType,

            LastModified = fileInfo.LastWriteTimeUtc,
            Created = fileInfo.CreationTimeUtc,

            ETag = GetBlobETagByFileInfo(fileInfo)
        };
    }

    public void CleanupBlobDirectoryChain(string filePath, string containerRoot)
    {
        var root = Path.GetFullPath(containerRoot);
        var current = Path.GetFullPath(Path.GetDirectoryName(filePath)!);

        while (true)
        {
            // stop at container root (never delete container)
            if (string.Equals(
                    current.TrimEnd(Path.DirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (!Directory.Exists(current))
            {
                break;
            }

            // if directory is NOT empty → stop
            if (Directory.EnumerateFileSystemEntries(current).Any())
            {
                break;
            }

            Directory.Delete(current);

            current = Path.GetDirectoryName(current)!;
        }
    }

    private static string ResolveBlobPath(string containerPath, string blob)
    {
        if (string.IsNullOrWhiteSpace(blob))
        {
            throw new InvalidOperationException("Blob name is empty.");
        }

        // 1. Normalize container root
        var rootPath = Path.GetFullPath(containerPath);

        // ensure trailing separator to avoid prefix issues
        rootPath = rootPath.TrimEnd(Path.DirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;

        // 2. Normalize blob separators (Azure style)
        blob = blob.Replace('\\', '/');

        // 3. Split into segments (prevents traversal tricks like "..")
        var segments = blob.Split('/', StringSplitOptions.RemoveEmptyEntries);

        var safeSegments = new List<string>(segments.Length);

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new InvalidOperationException($"Invalid blob segment. Segment: '{segment}'");
            }

            // optional: block weird control chars
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new InvalidOperationException("Invalid blob segment.");
            }

            safeSegments.Add(segment);
        }

        // 4. Rebuild safe relative path
        var safeRelativePath = Path.Combine(safeSegments.ToArray());

        // 5. Combine with container root
        var fullPath = Path.GetFullPath(Path.Combine(rootPath, safeRelativePath));

        // 6. HARD boundary check
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Path traversal detected.");
        }

        return fullPath;
    }

    private static string GetBlobETagByFileInfo(FileInfo fileInfo)
        => $"{fileInfo.Length}-{fileInfo.LastWriteTimeUtc.Ticks}";

    private Dictionary<string, string> BuildContainers()
    {
        var result = new Dictionary<string, string>
        {
            { Constants.BlobChangeFeedContainerName, Constants.BlobChangeFeedContainerName },
            { Constants.LogsContainerName, Constants.LogsContainerName }
        };

        foreach (var d in options.Value.Containers)
        {
            if (Directory.Exists(d.LocalPath))
            {
                result.Add(d.Name, d.LocalPath);
            }
        }

        if (Directory.Exists(options.Value.LocalPath))
        {
            foreach (var d in Directory.GetDirectories(options.Value.LocalPath))
            {
                result[Path.GetFileName(d)] = d;
            }
        }

        return result;
    }
}
