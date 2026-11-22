using System.Security.Cryptography;

namespace SecureSync.Core.Crypto;

/// <summary>
/// The sync chain key: 256 random bits that both identify a chain and encrypt everything in it. Written as
/// Base58Check(2-byte version + 32-byte secret), like a Bitcoin private key. The version makes every key exactly
/// 52 characters long and start with "SS"; the 4-byte checksum catches typos.
///
/// Because the key is fully random (not chosen by a person) it needs no password stretching: HKDF alone derives
/// the working keys. It must be kept secret — anyone who has it can read the chain.
/// </summary>
public sealed class ChainKey : IDisposable
{
    public const int SecretSize = 32;
    public const int TextLength = 52;
    private static ReadOnlySpan<byte> Version => [0xAB, 0xF8];

    private readonly byte[] _secret;

    private ChainKey(byte[] secret)
    {
        _secret = secret;
        Fingerprint = ComputeFingerprint(secret);
    }

    internal ReadOnlySpan<byte> Secret => _secret;

    /// <summary>
    /// A short, non-secret name for the chain (derived one-way from the key), e.g. "7F3A-91C2-04BD". Safe to show and
    /// store; used to recognise a vault folder and to tell chains apart.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>The full secret key as text. Only show this to the user deliberately.</summary>
    public string Reveal() => Base58.EncodeCheck([.. Version, .. _secret]);

    public static ChainKey Generate() => new(RandomNumberGenerator.GetBytes(SecretSize));

    public static ChainKey FromSecret(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretSize) throw new ArgumentException("Wrong key length.", nameof(secret));
        return new ChainKey(secret.ToArray());
    }

    /// <summary>Parses a typed or pasted key. Spaces, dashes and line breaks are ignored.</summary>
    public static bool TryParse(string? text, out ChainKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var cleaned = string.Concat(text.Where(c => !char.IsWhiteSpace(c) && c != '-'));
        if (!Base58.TryDecodeCheck(cleaned, out var raw)) return false;
        try
        {
            if (raw.Length != Version.Length + SecretSize || !raw.AsSpan(0, Version.Length).SequenceEqual(Version)) return false;
            key = new ChainKey(raw[Version.Length..]);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(raw);
        }
    }

    private static string ComputeFingerprint(byte[] secret)
    {
        var bytes = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 6, salt: null, "SecureSync/v2/fingerprint"u8.ToArray());
        var hex = Convert.ToHexString(bytes);
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..]}";
    }

    public bool SameChainAs(ChainKey other) => CryptographicOperations.FixedTimeEquals(_secret, other._secret);

    public void Dispose() => CryptographicOperations.ZeroMemory(_secret);
}
