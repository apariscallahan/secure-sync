using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SecureSync.Core.Crypto;

namespace SecureSync.Core.Security;

/// <summary>
/// Something that signs data only after the person holding the device proves who they are: Windows Hello on Windows,
/// BiometricPrompt (fingerprint, face, PIN, pattern or password) on Android. The signature must be deterministic (the
/// same data always gives the same signature), because it is used to derive the key that unwraps the stored chain key.
/// </summary>
public interface IKeySigner
{
    Task<SignerAvailability> CheckAvailabilityAsync();
    Task<byte[]> SignAsync(byte[] data, CancellationToken ct);
}

public enum SignerAvailability { Available, NotSetUp, NotSupported }

/// <summary>
/// An extra, platform-specific seal around the stored key (Windows: DPAPI, binding it to the Windows account).
/// Optional: Android keeps the profile in the app's private storage instead.
/// </summary>
public interface ILocalProtector
{
    byte[] Protect(byte[] data, byte[] entropy);
    byte[] Unprotect(byte[] data, byte[] entropy);
}

public class KeyStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The user dismissed the Windows Hello / biometric prompt.</summary>
public sealed class KeyStoreCanceledException() : KeyStoreException("Verification was canceled.");

/// <summary>
/// This device's link to a sync chain: where the vault folder is, what this device is called, whether it syncs, and the
/// chain key sealed so it can only be opened after the device's own user verification. Nothing in it is usable without
/// passing Windows Hello / the biometric prompt on this device.
/// </summary>
public sealed record LocalProfile
{
    public int Format { get; init; } = 2;
    public required string Folder { get; init; }
    public required string DeviceName { get; init; }
    public required string Fingerprint { get; init; }
    public required string SealedKey { get; init; }
    public required string Salt { get; init; }
    public bool SyncEnabled { get; init; } = true;
    /// <summary>Addresses reached with "Connect by IP address" (e.g. "192.168.1.20:47821"), retried automatically.</summary>
    public string[] RememberedPeers { get; init; } = [];

    public IReadOnlyList<System.Net.IPEndPoint> RememberedEndPoints() =>
        [.. RememberedPeers.Select(p => System.Net.IPEndPoint.TryParse(p, out var e) ? e : null).OfType<System.Net.IPEndPoint>()];

    /// <summary>Saves a newly reached "Connect by IP" address to the profile at <paramref name="path"/> (most recent first, max 8).</summary>
    public static void Remember(string path, System.Net.IPEndPoint endpoint)
    {
        if (Load(path) is not { } profile) return;
        var text = endpoint.ToString();
        if (profile.RememberedPeers.FirstOrDefault() == text) return;
        (profile with { RememberedPeers = [text, .. profile.RememberedPeers.Where(p => p != text).Take(7)] }).Save(path);
    }
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SecureSync", "profile.json");

    public static LocalProfile? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<LocalProfile>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Seals the chain key for storage on a device:
///
///   1. User verification: sign challenge = SHA-256("SecureSync/v2/hello-challenge" || salt) with the device's
///      verification-gated key (Windows Hello key in the TPM; Android Keystore HMAC key). The signature becomes a
///      wrapping key via HKDF and the chain key is encrypted with AES-256-GCM.
///   2. Optionally, a platform seal on top (DPAPI on Windows, binding it to the Windows account).
///
/// Reading the profile file alone gives an attacker nothing; they'd need the device's user session *and* to pass
/// its user verification.
/// </summary>
public static class KeyProtection
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static async Task<LocalProfile> SealAsync(ChainKey key, string folder, string deviceName, IKeySigner signer,
        ILocalProtector? protector = null, CancellationToken ct = default)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var wrapKey = await DeriveWrapKeyAsync(signer, salt, ct);
        var secret = key.Secret.ToArray();
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var sealedBytes = new byte[NonceSize + TagSize + secret.Length];
            nonce.CopyTo(sealedBytes, 0);
            using (var aes = new AesGcm(wrapKey, TagSize))
                aes.Encrypt(nonce, secret, sealedBytes.AsSpan(NonceSize + TagSize), sealedBytes.AsSpan(NonceSize, TagSize), Aad(key.Fingerprint));
            var stored = protector?.Protect(sealedBytes, salt) ?? sealedBytes;
            return new LocalProfile
            {
                Folder = folder,
                DeviceName = deviceName,
                Fingerprint = key.Fingerprint,
                SealedKey = Convert.ToBase64String(stored),
                Salt = Convert.ToBase64String(salt),
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public static async Task<ChainKey> UnsealAsync(LocalProfile profile, IKeySigner signer, ILocalProtector? protector = null,
        CancellationToken ct = default)
    {
        byte[] salt, sealedBytes;
        try
        {
            salt = Convert.FromBase64String(profile.Salt);
            var stored = Convert.FromBase64String(profile.SealedKey);
            sealedBytes = protector?.Unprotect(stored, salt) ?? stored;
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            throw new KeyStoreException("The saved sync chain key can't be opened by this user account. Enter your sync chain key to set this device up again.", e);
        }
        if (sealedBytes.Length != NonceSize + TagSize + ChainKey.SecretSize)
            throw new KeyStoreException("The saved sync chain key is damaged. Enter your sync chain key to set this device up again.");

        var wrapKey = await DeriveWrapKeyAsync(signer, salt, ct);
        var secret = new byte[ChainKey.SecretSize];
        try
        {
            using (var aes = new AesGcm(wrapKey, TagSize))
                aes.Decrypt(sealedBytes.AsSpan(0, NonceSize), sealedBytes.AsSpan(NonceSize + TagSize),
                    sealedBytes.AsSpan(NonceSize, TagSize), secret, Aad(profile.Fingerprint));
            var key = ChainKey.FromSecret(secret);
            if (key.Fingerprint != profile.Fingerprint)
            {
                key.Dispose();
                throw new KeyStoreException("The saved sync chain key doesn't match. Enter your sync chain key to set this device up again.");
            }
            return key;
        }
        catch (CryptographicException e)
        {
            // Happens if the device's verification key was reset (e.g. Windows Hello PIN reset, Android screen lock removed).
            throw new KeyStoreException("This device's sign-in (Windows Hello / screen lock) has been reset since SecureSync was set up, so the saved key can't be opened. Enter your sync chain key to set this device up again.", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapKey);
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(sealedBytes);
        }
    }

    private static async Task<byte[]> DeriveWrapKeyAsync(IKeySigner signer, byte[] salt, CancellationToken ct)
    {
        var challenge = SHA256.HashData([.. "SecureSync/v2/hello-challenge"u8, .. salt]);
        var signature = await signer.SignAsync(challenge, ct);
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, signature, 32, salt, "SecureSync/v2/hello-wrap"u8.ToArray());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
        }
    }

    private static byte[] Aad(string fingerprint) => Encoding.ASCII.GetBytes("SecureSync/v2/sealed-key|" + fingerprint);
}
