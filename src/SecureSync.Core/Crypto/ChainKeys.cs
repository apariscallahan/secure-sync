using System.Security.Cryptography;

namespace SecureSync.Core.Crypto;

/// <summary>
/// Working keys derived from the chain key with HKDF-SHA256. Every device holding the chain key derives the same
/// keys, so encrypted objects can be copied between devices byte-for-byte and still decrypt.
///
///   fileKey       encrypts file contents and file/folder records      info "SecureSync/v2/file-key"
///   authKey       proves to another device that we hold the chain key info "SecureSync/v2/peer-auth"
///   discoveryTag  lets devices on the same chain find each other      info "SecureSync/v2/discovery"
/// </summary>
public sealed class ChainKeys : IDisposable
{
    private readonly byte[] _fileKey;
    private readonly byte[] _authKey;

    public ChainKeys(ChainKey key)
    {
        var secret = key.Secret.ToArray();
        try
        {
            _fileKey = Derive(secret, 32, "SecureSync/v2/file-key");
            _authKey = Derive(secret, 32, "SecureSync/v2/peer-auth");
            DiscoveryTag = Derive(secret, 16, "SecureSync/v2/discovery");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
        Fingerprint = key.Fingerprint;
    }

    public string Fingerprint { get; }
    internal ReadOnlySpan<byte> FileKey => _fileKey;
    internal ReadOnlySpan<byte> AuthKey => _authKey;
    public byte[] DiscoveryTag { get; }

    private static byte[] Derive(byte[] secret, int length, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, length, salt: null, System.Text.Encoding.ASCII.GetBytes(info));

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_fileKey);
        CryptographicOperations.ZeroMemory(_authKey);
    }
}
