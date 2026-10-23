using Adliance.AzureBlobSimulator.Models;
using Adliance.AzureBlobSimulator.Services;
using Microsoft.Extensions.Options;

namespace Adliance.AzureBlobSimulator.Tests.SharedAccessSignature;

/// <summary>
/// Helper class for generating Shared Access Signatures (SAS) for tests.
/// </summary>
/// <param name="options">The storage options containing account information.</param>
/// <param name="validator">The SAS validator service used to generate signatures.</param>
public class SasHelper(IOptions<StorageOptions> options, SasValidatorService validator)
{
    private const string CanonicalizedResourceContainer = "/blob/testaccount/mycontainer"; // sr=c
    private const string CanonicalizedResourceBlob = "/blob/testaccount/mycontainer/myblob.txt"; // sr=b

    /// <summary>
    /// Generates a Shared Access Signature (SAS) for a blob or container.
    /// </summary>
    /// <param name="sp">Signed permissions.</param>
    /// <param name="st">Start time (optional).</param>
    /// <param name="se">Expiry time.</param>
    /// <param name="spr">Signed protocols (optional).</param>
    /// <param name="sv">Signed version.</param>
    /// <param name="sr">Resource type: "c" for container, "b" for blob.</param>
    /// <param name="canonicalizedResource">Optional canonicalized resource path.</param>
    /// <param name="accountKey">Optional account key; defaults to test account key.</param>
    /// <param name="accountName">Optional account name; defaults to "testaccount".</param>
    /// <returns>The generated SAS token as a string.</returns>
    public string GenerateSignature(
        string sp,
        string? st,
        string se,
        string? spr,
        string sv,
        string sr,
        string? canonicalizedResource = null,
        string? accountKey = null,
        string accountName = "testaccount")
    {
        accountKey ??= options.Value.Accounts.FirstOrDefault(a => a.Name.Equals(accountName))?.Key;
        Assert.NotNull(accountKey);

        return sr switch
        {
            "c" => validator.GenerateSignature(
                sp, st, se, canonicalizedResource ?? CanonicalizedResourceContainer, spr, sv, sr, Convert.FromBase64String(accountKey)),
            "b" => validator.GenerateSignature(
                sp, st, se, canonicalizedResource ?? CanonicalizedResourceBlob, spr, sv, sr, Convert.FromBase64String(accountKey)),
            _ => throw new ArgumentException("Invalid sr value. Must be 'c' or 'b'.", nameof(sr))
        };
    }
}
