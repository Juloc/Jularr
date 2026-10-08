using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Web.Features.Acquisition;

/// <summary>
/// Reads a stored secret (an indexer API key, a download client password). When the Data Protection key ring changed (a recreated volume, a restore
/// without the keys) the secret cannot be decrypted; that must not take the Usenet pages, the Wanted pass or the dashboard down. The secret then
/// counts as absent: the indexer or client reports an authentication problem, and the owner enters it again.
/// </summary>
public static class ProtectedSecrets
{
    public static string? Read(IDataProtector protector, string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        try
        {
            return protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
