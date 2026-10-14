using System.Security.Cryptography;
using System.Text;
using Jularr.Backend.Data.Logic.Storage;
using Jularr.Integrations.Data.Nas;
using Microsoft.AspNetCore.DataProtection;

namespace Jularr.Backend.Service.Core
{
    // Helper requests for a stored connection; the password is decrypted for Connect only.
    internal static class StorageMountRequests
    {
        public const string ProtectorPurpose = "Jularr.Storage.Password.V1";

        public static NasMountRequest Create(StorageMountData mount, NasMountAction action, IDataProtector protector)
        {
            string? password = null;
            if (action == NasMountAction.Connect && mount.ProtectedPassword is { } protectedPassword)
            {
                byte[] clear;
                try
                {
                    clear = protector.Unprotect(protectedPassword);
                }
                catch (CryptographicException)
                {
                    throw new NasMountException("storage_unavailable");
                }

                try
                {
                    password = Encoding.UTF8.GetString(clear);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clear);
                }
            }

            return new NasMountRequest(action, mount.Id, mount.Version, mount.StorageProtocolTypeId.ToString(), mount.Host!, mount.SharePath, mount.ProtocolVersion!, mount.Username,
                mount.Domain, password, mount.IsReadOnly);
        }
    }
}
