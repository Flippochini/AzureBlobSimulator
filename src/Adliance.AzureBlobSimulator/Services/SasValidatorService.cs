using System.Security.Cryptography;
using System.Text;
using Adliance.AzureBlobSimulator.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Adliance.AzureBlobSimulator.Services;

public sealed class SasValidatorService(
    IOptions<StorageOptions> options,
    ILogger<SasValidatorService>? logger,
    IHostEnvironment environment)
{
    private readonly StorageOptions _options = options.Value;

    /// <summary>
    /// Validates the SAS (Shared Access Signature) of an incoming HTTP request.
    /// Checks the signature, required parameters, start and expiry times,
    /// canonicalized resource, and permission alignment with the HTTP method.
    /// </summary>
    /// <param name="request">The HTTP request containing SAS query parameters.</param>
    /// <returns>A <see cref="SasValidationResult"/> indicating success or failure of validation.</returns>
    public SasValidationResult Validate(HttpRequest request)
    {
        var account = ResolveAccount(request);
        if (account == null)
            return SasValidationResult.Fail("AuthenticationFailed", "Unknown storage account.");

        var accountKeyBytes = Convert.FromBase64String(account.Key);

        var query = request.Query;

        if (!query.TryGetValue("sig", out var sigValues) ||
            StringValues.IsNullOrEmpty(sigValues))
        {
            return SasValidationResult.Fail("AuthenticationFailed", "Missing signature.");
        }

        var signature = sigValues.ToString();

        if (!TryGetRequired(query, "sv", out var sv) ||
            !TryGetRequired(query, "sp", out var sp) ||
            !TryGetRequired(query, "se", out var se) ||
            !TryGetRequired(query, "sr", out var sr))
        {
            return SasValidationResult.Fail("AuthenticationFailed", "Missing required SAS parameters.");
        }

        TryGetOptional(query, "st", out var st);
        TryGetOptional(query, "spr", out var spr);
        TryGetOptional(query, "si", out var si);
        TryGetOptional(query, "sip", out var sip);
        TryGetOptional(query, "ses", out var ses);
        TryGetOptional(query, "rscc", out var rscc);
        TryGetOptional(query, "rscd", out var rscd);
        TryGetOptional(query, "rsce", out var rsce);
        TryGetOptional(query, "rscl", out var rscl);
        TryGetOptional(query, "rsct", out var rsct);

        var now = DateTimeOffset.UtcNow;

        if (!string.IsNullOrEmpty(st) &&
            DateTimeOffset.TryParse(st, out var startTime))
        {
            if (now < startTime)
                return SasValidationResult.Fail("AuthenticationFailed", "SAS not yet valid.");
        }

        if (!DateTimeOffset.TryParse(se, out var expiryTime))
        {
            return SasValidationResult.Fail("AuthenticationFailed", "Invalid expiry time.");
        }

        if (now > expiryTime)
        {
            return SasValidationResult.Fail("AuthenticationFailed", "SAS expired.");
        }

        var canonicalizedResource = BuildCanonicalizedResource(request, account.Name, sr);
        if (canonicalizedResource == null)
        {
            return SasValidationResult.Fail("AuthenticationFailed", "Invalid resource.");
        }

        var stringToSign = BuildStringToSign(sp, st, se, canonicalizedResource, spr, sv, sr, si, sip, ses: ses, rscc: rscc, rscd: rscd, rsce: rsce, rscl: rscl, rsct: rsct);
        var computedSignature = ComputeSignature(stringToSign, accountKeyBytes);

        if (environment.IsDevelopment())
        {
            logger?.LogInformation(
                """
                SAS DEBUG
                Account: {Account}
                CanonicalizedResource: {CanonicalizedResource}
                StringToSign:
                {StringToSign}
                ComputedSignature: {ComputedSignature}
                ProvidedSignature: {ProvidedSignature}
                """,
                account.Name,
                canonicalizedResource,
                EscapeNewlines(stringToSign),
                computedSignature,
                signature
            );
        }

        if (!SignaturesEqual(computedSignature, signature))
        {
            // Previous versions of the simulator always used the string-to-sign layout of version 2015-04-05, regardless of sv.
            // SAS tokens generated that way are still accepted, so existing SAS URLs don't break.
            var legacyStringToSign = BuildStringToSign(sp, st, se, canonicalizedResource, spr, sv, sr, si, sip, ses: ses, rscc: rscc, rscd: rscd, rsce: rsce, rscl: rscl, rsct: rsct, useLegacyLayout: true);
            if (!SignaturesEqual(ComputeSignature(legacyStringToSign, accountKeyBytes), signature))
            {
                return SasValidationResult.Fail("AuthenticationFailed", "Signature mismatch.");
            }
        }

        if (!IsPermissionAllowed(request.Method, sp))
        {
            return SasValidationResult.Fail("AuthorizationPermissionMismatch", "Permission denied.");
        }

        return SasValidationResult.Success();
    }

    /// <summary>
    /// Generates a SAS signature string based on provided SAS parameters and account key.
    /// </summary>
    /// <param name="sp">Permissions string (e.g., "rwd").</param>
    /// <param name="st">Optional start time of SAS validity.</param>
    /// <param name="se">Expiry time of SAS.</param>
    /// <param name="canonicalizedResource">Canonicalized resource path.</param>
    /// <param name="spr">Optional signed protocol (e.g., "https").</param>
    /// <param name="sv">SAS version.</param>
    /// <param name="sr">Signed resource ("b" for blob, "c" for container).</param>
    /// <param name="key">Account key as a byte array.</param>
    /// <returns>The computed Base64-encoded signature string.</returns>
    public string GenerateSignature(string sp,
        string? st,
        string se,
        string canonicalizedResource,
        string? spr,
        string sv,
        string sr,
        byte[] key)
    {
        var sts = BuildStringToSign(sp, st, se, canonicalizedResource, spr, sv, sr);
        return ComputeSignature(sts, key);
    }

    /// <summary>
    /// Resolves the storage account from the HTTP request context.
    /// </summary>
    /// <param name="request">The incoming HTTP request.</param>
    /// <returns>The matched <see cref="StorageAccountOptions"/> or null if not found.</returns>
    private StorageAccountOptions? ResolveAccount(HttpRequest request)
    {
        var possibleAccountName = request.HttpContext.Items["account"] as string;

        if (string.IsNullOrEmpty(possibleAccountName))
        {
            return null;
        }

        var account = _options.Accounts.FirstOrDefault(a =>
            string.Equals(a.Name, possibleAccountName, StringComparison.OrdinalIgnoreCase));

        return account;
    }

    /// <summary>
    /// Builds the canonicalized resource string required for SAS signature computation.
    /// </summary>
    /// <param name="request">The HTTP request containing the resource path.</param>
    /// <param name="accountName">The storage account name.</param>
    /// <param name="sr">Resource type ("b" for blob, "c" for container).</param>
    /// <returns>The canonicalized resource string or null if invalid.</returns>
    private static string? BuildCanonicalizedResource(HttpRequest request, string accountName, string sr)
    {
        var path = request.Path.Value;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        // Remove the account segment if present
        if (segments[0].Equals(accountName, StringComparison.OrdinalIgnoreCase))
        {
            segments = segments.Skip(1).ToArray();
        }

        if (segments.Length == 0)
        {
            return null;
        }

        var container = segments[0];

        switch (sr)
        {
            case "c": // container-level SAS
                return $"/blob/{accountName}/{container}";
            case "b": // blob-level SAS
                if (segments.Length < 2)
                {
                    return null; // invalid blob path
                }

                var blobPath = string.Join("/", segments.Skip(1));
                return $"/blob/{accountName}/{container}/{blobPath}";
            default:
                return null;
        }
    }

    /// <summary>
    /// Builds the canonical string-to-sign used to generate a Shared Access Signature (SAS)
    /// for Azure Blob Storage.
    /// </summary>
    /// <remarks>
    /// The string is constructed in the exact order defined by the Azure Storage
    /// service specification and must include newline separators between each field,
    /// including empty values for optional parameters. The resulting string is later
    /// hashed (HMAC-SHA256) with the storage account key to produce the <c>sig</c> value.
    ///
    /// The layout depends on the SAS version (sv): version 2018-11-09 adds signedResource and signedSnapshotTime,
    /// version 2020-12-06 adds signedEncryptionScope. Versions before 2015-04-05 are not supported, see
    /// https://learn.microsoft.com/en-us/rest/api/storageservices/create-service-sas#construct-the-signature-string
    ///
    /// All parameters must already be normalized and formatted according to the
    /// expected SAS version.
    /// </remarks>
    /// <param name="sp">Signed permissions.</param>
    /// <param name="st">Signed start time (optional).</param>
    /// <param name="se">Signed expiry time.</param>
    /// <param name="canonicalizedResource">Canonicalized resource path (e.g., /blob/{account}/{container}/{blob}).</param>
    /// <param name="spr">Signed protocol (optional).</param>
    /// <param name="sv">Signed service version.</param>
    /// <param name="sr">Signed resource ("b" for blob, "c" for container).</param>
    /// <param name="si">Signed identifier (optional).</param>
    /// <param name="sip">Signed IP range (optional).</param>
    /// <param name="signedSnapshotTime">Snapshot time or version id (optional, only for snapshot and version resources, which are not supported by the simulator).</param>
    /// <param name="ses">Signed encryption scope (optional).</param>
    /// <param name="rscc">Cache-Control response header override (optional).</param>
    /// <param name="rscd">Content-Disposition response header override (optional).</param>
    /// <param name="rsce">Content-Encoding response header override (optional).</param>
    /// <param name="rscl">Content-Language response header override (optional).</param>
    /// <param name="rsct">Content-Type response header override (optional).</param>
    /// <param name="useLegacyLayout">Use the layout of version 2015-04-05 regardless of sv, as previous versions of the simulator did.</param>
    /// <returns>The fully constructed string-to-sign.</returns>
    private static string BuildStringToSign(
        string sp, // permissions
        string? st, // start time (optional)
        string se, // expiry time
        string canonicalizedResource,
        string? spr, // signed protocol (optional)
        string sv, // SAS version
        string sr, // signed resource
        string? si = null, // signed identifier (optional)
        string? sip = null, // signed IP (optional)
        string? signedSnapshotTime = null, // snapshot time or version id (optional)
        string? ses = null, // signed encryption scope (optional)
        string? rscc = null,
        string? rscd = null,
        string? rsce = null,
        string? rscl = null,
        string? rsct = null,
        bool useLegacyLayout = false
    )
    {
        var sb = new StringBuilder();

        sb.Append(sp).Append('\n');
        sb.Append(st ?? string.Empty).Append('\n');
        sb.Append(se).Append('\n');
        sb.Append(canonicalizedResource).Append('\n');
        sb.Append(si ?? string.Empty).Append('\n');
        sb.Append(sip ?? string.Empty).Append('\n');
        sb.Append(spr ?? string.Empty).Append('\n');
        sb.Append(sv).Append('\n');

        if (!useLegacyLayout && IsVersionAtLeast(sv, "2018-11-09"))
        {
            sb.Append(sr).Append('\n');
            sb.Append(signedSnapshotTime ?? string.Empty).Append('\n');
        }

        if (!useLegacyLayout && IsVersionAtLeast(sv, "2020-12-06"))
        {
            sb.Append(ses ?? string.Empty).Append('\n');
        }

        sb.Append(rscc ?? string.Empty).Append('\n');
        sb.Append(rscd ?? string.Empty).Append('\n');
        sb.Append(rsce ?? string.Empty).Append('\n');
        sb.Append(rscl ?? string.Empty).Append('\n');
        sb.Append(rsct ?? string.Empty); // no newline at the end

        return sb.ToString();
    }

    /// <summary>
    /// Checks whether a SAS version is the same as or later than the given minimum version.
    /// </summary>
    /// <remarks>
    /// SAS versions have the format yyyy-MM-dd, so an ordinal comparison orders them chronologically.
    /// </remarks>
    /// <param name="version">The SAS version (sv).</param>
    /// <param name="minimumVersion">The minimum version, e.g. "2018-11-09".</param>
    /// <returns>True if the version is the same as or later than the minimum version; otherwise, false.</returns>
    private static bool IsVersionAtLeast(string version, string minimumVersion)
        => string.CompareOrdinal(version, minimumVersion) >= 0;

    /// <summary>
    /// Computes the HMAC-SHA256 signature of a string using a given key.
    /// </summary>
    /// <param name="stringToSign">The string to compute the signature for.</param>
    /// <param name="key">The key as a byte array.</param>
    /// <returns>The Base64-encoded signature string.</returns>
    private static string ComputeSignature(string stringToSign, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        var stringBytes = Encoding.UTF8.GetBytes(stringToSign);
        var hash = hmac.ComputeHash(stringBytes);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Compares two Base64-encoded signatures in a time-constant manner to prevent timing attacks.
    /// </summary>
    /// <param name="computed">The computed signature.</param>
    /// <param name="provided">The signature provided in the request.</param>
    /// <returns>True if the signatures match; otherwise, false.</returns>
    private static bool SignaturesEqual(string computed, string provided)
    {
        try
        {
            var computedBytes = Convert.FromBase64String(computed);
            var providedBytes = Convert.FromBase64String(provided);

            return CryptographicOperations.FixedTimeEquals(computedBytes, providedBytes);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks whether the HTTP method is allowed based on the SAS permissions string.
    /// </summary>
    /// <param name="method">The HTTP method of the request (GET, PUT, DELETE, HEAD).</param>
    /// <param name="sp">The permissions string from the SAS.</param>
    /// <returns>True if the method is allowed; otherwise, false.</returns>
    private static bool IsPermissionAllowed(string? method, string sp)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return false;
        }

        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            return sp.Contains('r') || sp.Contains('l');
        }

        if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
        {
            return sp.Contains('w') || sp.Contains('c');
        }

        if (method.Equals("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            return sp.Contains('d');
        }

        return method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) && sp.Contains('r');
    }

    /// <summary>
    /// Attempts to retrieve a required query parameter from the HTTP request.
    /// </summary>
    /// <param name="query">The query collection.</param>
    /// <param name="key">The key of the parameter to retrieve.</param>
    /// <param name="value">Outputs the value if found.</param>
    /// <returns>True if the parameter exists and has a value; otherwise, false.</returns>
    private static bool TryGetRequired(IQueryCollection query, string key, out string value)
    {
        if (query.TryGetValue(key, out var values) &&
            !StringValues.IsNullOrEmpty(values))
        {
            value = values.ToString();
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Attempts to retrieve an optional query parameter from the HTTP request.
    /// </summary>
    /// <param name="query">The query collection.</param>
    /// <param name="key">The key of the parameter to retrieve.</param>
    /// <param name="value">Outputs the value if found, or null if not present.</param>
    /// <returns>True if the parameter exists and has a value; otherwise, false.</returns>
    private static bool TryGetOptional(IQueryCollection query, string key, out string? value)
    {
        if (query.TryGetValue(key, out var values) &&
            !StringValues.IsNullOrEmpty(values))
        {
            value = values.ToString();
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Escapes newline characters in a string for logging purposes.
    /// Replaces '\r' with '\\r' and '\n' with '\\n\n'.
    /// </summary>
    /// <param name="input">The input string to escape.</param>
    /// <returns>The escaped string suitable for logging.</returns>
    private static string EscapeNewlines(string input)
        => input.Replace("\r", "\\r").Replace("\n", "\\n\n");
}
