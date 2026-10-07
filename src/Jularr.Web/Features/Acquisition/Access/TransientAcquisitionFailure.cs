using System.Data.Common;
using System.Net.Http;
using System.Security.Cryptography;

namespace Jularr.Web.Features.Acquisition.Access;

/// <summary>
/// Tells an infrastructure problem of this server (the database, storage, a service that did not answer, an unreadable stored secret) from a
/// failure of the request itself. The first is not the request's fault: it stays wanted and is tried again, with a plain sentence instead of the
/// exception text; only the second ends the request until the owner retries it.
/// </summary>
public static class TransientAcquisitionFailure
{
    public static string? Describe(Exception exception) => exception switch
    {
        CryptographicException => "A stored credential could not be read because the encryption keys changed. Enter it again in Admin.",
        DbException => "The database was not available.",
        IOException or UnauthorizedAccessException => "Storage was not available.",
        HttpRequestException or TimeoutException => "A service did not answer.",
        _ => null
    };
}
