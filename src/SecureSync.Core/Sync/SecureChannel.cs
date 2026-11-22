using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SecureSync.Core.Sync;

/// <summary>
/// Encrypted, authenticated message framing over a TCP stream, set up by <see cref="Handshake"/>.
/// Frame: uint32 LE length | AES-256-GCM(type byte | body) | tag. The length prefix is the AAD, the nonce is a
/// per-direction counter, so frames cannot be modified, dropped, replayed or reordered without detection.
/// </summary>
internal sealed class SecureChannel : IDisposable
{
    public const int MaxBody = 1024 * 1024;
    private const int TagSize = 16;

    private readonly Stream _stream;
    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ulong _sendCounter;
    private ulong _receiveCounter;

    public SecureChannel(Stream stream, byte[] sendKey, byte[] receiveKey)
    {
        _stream = stream;
        _send = new AesGcm(sendKey, TagSize);
        _receive = new AesGcm(receiveKey, TagSize);
        CryptographicOperations.ZeroMemory(sendKey);
        CryptographicOperations.ZeroMemory(receiveKey);
    }

    public async Task SendAsync(MessageType type, ReadOnlyMemory<byte> body, CancellationToken ct)
    {
        if (body.Length > MaxBody) throw new ArgumentException("Message too large.", nameof(body));
        int plainLength = 1 + body.Length;
        var plain = new byte[plainLength];
        plain[0] = (byte)type;
        body.CopyTo(plain.AsMemory(1));
        var frame = new byte[4 + plainLength + TagSize];

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            BinaryPrimitives.WriteInt32LittleEndian(frame, plainLength + TagSize);
            _send.Encrypt(Nonce(_sendCounter++), plain, frame.AsSpan(4, plainLength), frame.AsSpan(4 + plainLength, TagSize),
                frame.AsSpan(0, 4));
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task<(MessageType Type, byte[] Body)> ReceiveAsync(CancellationToken ct)
    {
        var lengthBytes = new byte[4];
        await _stream.ReadExactlyAsync(lengthBytes, ct).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length < 1 + TagSize || length > 1 + MaxBody + TagSize)
            throw new ProtocolException("Invalid frame length.");
        var frame = new byte[length];
        await _stream.ReadExactlyAsync(frame, ct).ConfigureAwait(false);
        int plainLength = length - TagSize;
        var plain = new byte[plainLength];
        try
        {
            _receive.Decrypt(Nonce(_receiveCounter++), frame.AsSpan(0, plainLength), frame.AsSpan(plainLength), plain, lengthBytes);
        }
        catch (CryptographicException e)
        {
            throw new ProtocolException("A network message failed authentication.", e);
        }
        return ((MessageType)plain[0], plain[1..]);
    }

    private static byte[] Nonce(ulong counter)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    public void Dispose()
    {
        _send.Dispose();
        _receive.Dispose();
        _sendLock.Dispose();
    }
}

internal enum MessageType : byte
{
    Info = 1,
    Inventory = 2,
    WantEntries = 3,
    Entry = 4,
    EntryMissing = 5,
    WantBlobs = 6,
    BlobBegin = 7,
    BlobData = 8,
    BlobEnd = 9,
    BlobMissing = 10,
    Status = 11,
    Ping = 12,
    Pong = 13,
    Digest = 14,
    InventoryRequest = 15,
}

public class ProtocolException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class PeerAuthenticationException(string message) : ProtocolException(message);
