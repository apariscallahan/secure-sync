using System.Buffers.Binary;
using System.Security.Cryptography;
using SecureSync.Core.Crypto;

namespace SecureSync.Core.Sync;

/// <summary>
/// Mutually authenticated key exchange between two devices that hold the same chain key.
///
///   I → R  hello_I = "SSYNHS02" | discoveryTag | nonce_I | ephemeral P-256 public key
///   R → I  hello_R = same
///          th  = SHA-256(hello_I | hello_R)
///          prk = HKDF-Extract(salt = authKey, ikm = ECDH(eph_I, eph_R))
///          k_I→R | k_R→I | k_confirm = HKDF-Expand(prk, "SecureSync/v2/session" | th, 96)
///   I → R  HMAC(k_confirm, "initiator" | th)         R checks it before answering (else sends 0x00 and hangs up)
///   R → I  HMAC(k_confirm, "responder" | th)
///
/// Only a device that derived authKey from the same chain key can produce the confirmations, and the ephemeral
/// ECDH gives each session fresh keys (forward secrecy). This is the shape of the Noise "NNpsk0" pattern.
/// </summary>
internal static class Handshake
{
    private static ReadOnlySpan<byte> Magic => "SSYNHS02"u8;
    private const int NonceSize = 32;
    private const int MaxFrame = 1024;
    private const byte RejectDifferentChain = 1;
    internal const string DifferentChainMessage = "That device is on a different sync chain.";

    public static async Task<SecureChannel> RunAsync(Stream stream, ChainKeys keys, bool initiator, CancellationToken ct)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var myHello = BuildHello(keys.DiscoveryTag, RandomNumberGenerator.GetBytes(NonceSize), ecdh.ExportSubjectPublicKeyInfo());

        byte[] peerHello;
        if (initiator)
        {
            await WriteFrameAsync(stream, myHello, ct);
            try
            {
                peerHello = await ReadFrameAsync(stream, ct);
            }
            catch (Exception e) when (e is EndOfStreamException or IOException)
            {
                throw new ProtocolException("That computer closed the connection without answering as a SecureSync device.", e);
            }
            if (peerHello is [RejectDifferentChain]) throw new ProtocolException(DifferentChainMessage);
            ParseHello(peerHello, keys, out var peerKey);
            return await FinishAsync(stream, keys, ecdh, peerKey, myHello, peerHello, initiator, ct);
        }
        else
        {
            peerHello = await ReadFrameAsync(stream, ct);
            byte[] peerKey;
            try
            {
                ParseHello(peerHello, keys, out peerKey);
            }
            catch (ProtocolException e) when (e.Message == DifferentChainMessage)
            {
                await WriteFrameAsync(stream, [RejectDifferentChain], ct); // so a manual connect can say why
                throw;
            }
            await WriteFrameAsync(stream, myHello, ct);
            return await FinishAsync(stream, keys, ecdh, peerKey, peerHello, myHello, initiator, ct);
        }
    }

    private static async Task<SecureChannel> FinishAsync(Stream stream, ChainKeys keys, ECDiffieHellman ecdh, byte[] peerKey,
        byte[] helloI, byte[] helloR, bool initiator, CancellationToken ct)
    {
        using var peer = ECDiffieHellman.Create();
        try
        {
            peer.ImportSubjectPublicKeyInfo(peerKey, out _);
        }
        catch (CryptographicException e)
        {
            throw new ProtocolException("Peer sent an invalid public key.", e);
        }
        if (peer.KeySize != 256) throw new ProtocolException("Peer used an unexpected key type.");

        var shared = ecdh.DeriveRawSecretAgreement(peer.PublicKey);
        var transcript = SHA256.HashData([.. helloI, .. helloR]);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, shared, keys.AuthKey.ToArray());
        var okm = HKDF.Expand(HashAlgorithmName.SHA256, prk, 96, [.. "SecureSync/v2/session"u8, .. transcript]);
        var keyIR = okm[..32];
        var keyRI = okm[32..64];
        var confirmKey = okm[64..96];
        var confirmI = HMACSHA256.HashData(confirmKey, (byte[])[.. "initiator"u8, .. transcript]);
        var confirmR = HMACSHA256.HashData(confirmKey, (byte[])[.. "responder"u8, .. transcript]);
        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(prk);
        CryptographicOperations.ZeroMemory(okm);
        CryptographicOperations.ZeroMemory(confirmKey);

        if (initiator)
        {
            await WriteFrameAsync(stream, confirmI, ct);
            var answer = await ReadFrameAsync(stream, ct);
            if (answer is [0])
                throw new PeerAuthenticationException("The other computer answered for this sync chain but couldn't prove it has the chain key.");
            if (!CryptographicOperations.FixedTimeEquals(answer, confirmR))
                throw new PeerAuthenticationException("The other computer couldn't prove it has the sync chain key.");
            return new SecureChannel(stream, keyIR, keyRI);
        }

        var claim = await ReadFrameAsync(stream, ct);
        if (!CryptographicOperations.FixedTimeEquals(claim, confirmI))
        {
            await WriteFrameAsync(stream, [0], ct);
            throw new PeerAuthenticationException("A computer tried to connect to this sync chain without the right chain key.");
        }
        await WriteFrameAsync(stream, confirmR, ct);
        return new SecureChannel(stream, keyRI, keyIR);
    }

    private static byte[] BuildHello(byte[] tag, byte[] nonce, byte[] publicKey)
    {
        var hello = new byte[Magic.Length + tag.Length + NonceSize + 2 + publicKey.Length];
        var span = hello.AsSpan();
        Magic.CopyTo(span);
        tag.CopyTo(span[8..]);
        nonce.CopyTo(span[(8 + tag.Length)..]);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(8 + tag.Length + NonceSize)..], (ushort)publicKey.Length);
        publicKey.CopyTo(span[(8 + tag.Length + NonceSize + 2)..]);
        return hello;
    }

    private static void ParseHello(byte[] hello, ChainKeys keys, out byte[] publicKey)
    {
        int tagLength = keys.DiscoveryTag.Length;
        int fixedLength = Magic.Length + tagLength + NonceSize + 2;
        if (hello.Length < fixedLength || !hello.AsSpan(0, 8).SequenceEqual(Magic))
            throw new ProtocolException("Not a SecureSync device.");
        if (!hello.AsSpan(8, tagLength).SequenceEqual(keys.DiscoveryTag))
            throw new ProtocolException(DifferentChainMessage);
        int keyLength = BinaryPrimitives.ReadUInt16LittleEndian(hello.AsSpan(fixedLength - 2));
        if (fixedLength + keyLength != hello.Length) throw new ProtocolException("Malformed handshake.");
        publicKey = hello[fixedLength..];
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] data, CancellationToken ct)
    {
        var frame = new byte[2 + data.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)data.Length);
        data.CopyTo(frame, 2);
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var lengthBytes = new byte[2];
        await stream.ReadExactlyAsync(lengthBytes, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(lengthBytes);
        if (length == 0 || length > MaxFrame) throw new ProtocolException("Malformed handshake.");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        return data;
    }
}
