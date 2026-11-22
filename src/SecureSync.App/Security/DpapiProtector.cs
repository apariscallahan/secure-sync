using System.Security.Cryptography;
using SecureSync.Core.Security;

namespace SecureSync.App.Security;

/// <summary>Windows DPAPI (current user): binds the stored, Windows Hello-sealed key to this Windows account as well.</summary>
internal sealed class DpapiProtector : ILocalProtector
{
    public byte[] Protect(byte[] data, byte[] entropy) => ProtectedData.Protect(data, entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data, byte[] entropy) => ProtectedData.Unprotect(data, entropy, DataProtectionScope.CurrentUser);
}
