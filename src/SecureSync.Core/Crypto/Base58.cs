using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace SecureSync.Core.Crypto;

/// <summary>Bitcoin-style Base58 and Base58Check encoding.</summary>
public static class Base58
{
    public const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var value = new BigInteger(data, isUnsigned: true, isBigEndian: true);
        var sb = new StringBuilder();
        while (value > 0)
        {
            value = BigInteger.DivRem(value, 58, out var rem);
            sb.Insert(0, Alphabet[(int)rem]);
        }
        foreach (var b in data)
        {
            if (b != 0) break;
            sb.Insert(0, '1');
        }
        return sb.ToString();
    }

    public static bool TryDecode(string text, out byte[] data)
    {
        data = [];
        BigInteger value = 0;
        foreach (var c in text)
        {
            int digit = Alphabet.IndexOf(c);
            if (digit < 0) return false;
            value = value * 58 + digit;
        }
        var body = value.IsZero ? [] : value.ToByteArray(isUnsigned: true, isBigEndian: true);
        int leadingZeros = text.TakeWhile(c => c == '1').Count();
        data = new byte[leadingZeros + body.Length];
        body.CopyTo(data, leadingZeros);
        return true;
    }

    public static string EncodeCheck(ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[payload.Length + 4];
        payload.CopyTo(buffer);
        Checksum(payload).CopyTo(buffer.AsSpan(payload.Length));
        return Encode(buffer);
    }

    public static bool TryDecodeCheck(string text, out byte[] payload)
    {
        payload = [];
        if (!TryDecode(text, out var raw) || raw.Length < 5) return false;
        var body = raw.AsSpan(0, raw.Length - 4);
        if (!CryptographicOperations.FixedTimeEquals(Checksum(body), raw.AsSpan(raw.Length - 4))) return false;
        payload = body.ToArray();
        return true;
    }

    private static byte[] Checksum(ReadOnlySpan<byte> payload) =>
        SHA256.HashData(SHA256.HashData(payload))[..4];
}
