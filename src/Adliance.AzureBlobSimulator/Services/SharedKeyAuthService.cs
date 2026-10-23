using System.Security.Cryptography;
using System.Text;
using Adliance.AzureBlobSimulator.Models;
using Microsoft.Extensions.Options;

namespace Adliance.AzureBlobSimulator.Services;

public class SharedKeyAuthService(ILogger<SharedKeyAuthService> logger, IOptions<StorageOptions> blobStorageOptions)
{
    public static bool HasSharedKeyAuth(HttpRequest request)
    {
        return request.Headers.Authorization.ToString().StartsWith("SharedKey ");
    }

    public bool ValidateSharedKeyAuth(HttpRequest request)
    {
        var authHeader = request.Headers.Authorization.ToString();
        var authValue = authHeader["SharedKey ".Length..];

        var parts = authValue.Split(':', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        var accountName = parts[0];
        var providedSignature = parts[1];

        var storageAccounts = blobStorageOptions.Value.Accounts;

        var matchingAccount = storageAccounts.FirstOrDefault(a => a.Name == accountName);
        if (matchingAccount == null)
        {
            logger.LogInformation("Unknown account \"{AccountName}\".", accountName);
            return false;
        }

        if (string.IsNullOrWhiteSpace(matchingAccount.Key))
        {
            logger.LogWarning("No access key configured for account \"{AccountName}\".", matchingAccount.Name);
            return false;
        }

        // Build string-to-sign
        var stringToSign = BuildSharedKeyStringToSign(request, accountName);
        var expectedSignature = CalculateHmacSha256(stringToSign, matchingAccount.Key);

        if (providedSignature == expectedSignature)
        {
            var pathSegments = request.Path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (pathSegments is { Length: >= 1 } && pathSegments[0].Equals(accountName))
            {
                request.Path = new PathString("/" + string.Join('/', pathSegments.Skip(1))); // remove account from path (implicit string conversion would URL-decode again)
            }
            logger.LogDebug("SharedKey validated successfully for account \"{AccountName}\".", accountName);
            return true;
        }

        logger.LogWarning("SharedKey signature mismatch for account \"{AccountName}\".", accountName);
        logger.LogTrace("String to sign=\"{StringToSign}\", expected signature=\"{Expected}\", provided signature=\"{Provided}\".", stringToSign, expectedSignature, providedSignature);

        return false;
    }

    private static string BuildSharedKeyStringToSign(HttpRequest request, string accountName)
    {
        var verb = request.Method.ToUpper();
        var contentEncoding = GetHeaderValue(request, "Content-Encoding");
        var contentLanguage = GetHeaderValue(request, "Content-Language");
        var contentLength = request.ContentLength?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "0";
        var contentMd5 = GetHeaderValue(request, "Content-MD5");
        var contentType = GetHeaderValue(request, "Content-Type");
        var ifModifiedSince = GetHeaderValue(request, "If-Modified-Since");
        var ifMatch = GetHeaderValue(request, "If-Match");
        var ifNoneMatch = GetHeaderValue(request, "If-None-Match");
        var ifUnmodifiedSince = GetHeaderValue(request, "If-Unmodified-Since");
        var range = GetHeaderValue(request, "Range");
        if (contentLength == "0") contentLength = "";
        var date = "";

        var canonicalizedHeaders = BuildCanonicalizedHeaders(request);
        var canonicalizedResource = BuildCanonicalizedResource(request, accountName);

        var stringToSign = $"{verb}\n{contentEncoding}\n{contentLanguage}\n{contentLength}\n" +
                           $"{contentMd5}\n{contentType}\n{date}\n{ifModifiedSince}\n" +
                           $"{ifMatch}\n{ifNoneMatch}\n{ifUnmodifiedSince}\n{range}\n" +
                           $"{canonicalizedHeaders}{canonicalizedResource}";

        return stringToSign;
    }

    private static string BuildCanonicalizedHeaders(HttpRequest request)
    {
        var canonicalizedHeaders = new SortedDictionary<string, string>();

        foreach (var header in request.Headers)
        {
            var headerName = header.Key.ToLowerInvariant();
            if (headerName.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase))
            {
                var headerValue = string.Join(",", header.Value.ToArray());
                canonicalizedHeaders[headerName] = headerValue.Trim();
            }
        }

        var result = new StringBuilder();
        foreach (var header in canonicalizedHeaders)
        {
            result.Append(System.Globalization.CultureInfo.InvariantCulture, $"{header.Key}:{header.Value}\n");
        }

        return result.ToString();
    }

    private static string BuildCanonicalizedResource(HttpRequest request, string accountName)
    {
        // The spec requires the path "encoded exactly as it is in the URI", but request.Path is already decoded
        // (e.g. "%2C" becomes ","), so we take the path from the raw request target instead.
        var resource = $"/{accountName}{GetEncodedPath(request)}";

        if (!request.QueryString.HasValue)
        {
            return resource;
        }

        var queryParams = new SortedDictionary<string, string>();

        foreach (var param in request.Query)
        {
            var key = param.Key.ToLowerInvariant();
            var value = string.Join(",", param.Value.Order(StringComparer.Ordinal));
            queryParams[key] = value;
        }

        foreach (var param in queryParams)
        {
            resource += $"\n{param.Key}:{param.Value}";
        }

        return resource;
    }

    private static string GetEncodedPath(HttpRequest request)
    {
        var path = request.PathBase.Add(request.Path).Value ?? "";
        return string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
    }

    private static string? GetHeaderValue(HttpRequest request, string headerName)
    {
        return request.Headers.TryGetValue(headerName, out var values) ? values.ToString() : null;
    }

    private static string CalculateHmacSha256(string stringToSign, string key)
    {
        var stringToSignBytes = Encoding.UTF8.GetBytes(stringToSign);
        var keyBytes = Convert.FromBase64String(key);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(stringToSignBytes);
        return Convert.ToBase64String(hashBytes);
    }
}
