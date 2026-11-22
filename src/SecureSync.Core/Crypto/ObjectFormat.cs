using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SecureSync.Core.Storage;

namespace SecureSync.Core.Crypto;

/// <summary>
/// On-disk format of encrypted objects. The same bytes are stored on every device.
///
///   header (64 bytes, authenticated as AAD everywhere):
///     0  magic "SSYNCOB2"      8  format version (2)     9  kind (1 entry, 2 blob)
///     10 reserved (2)          12 chunk size (uint32 LE) 16 object id (16)   32 key salt (32 random bytes)
///
///   entry (".ssentry", a file or folder record):
///     uint32 length | AES-256-GCM( uint32 jsonLength | json | zero padding to 256 bytes ) | tag
///   blob (".ssblob", file contents, ≥ 1 chunk):
///     repeated: uint32 length | AES-256-GCM( up to chunkSize bytes ) | tag
///
/// objectKey = HKDF-SHA256(fileKey, salt = key salt, info = "SecureSync/v2/object" || header)
/// nonce     = domain byte | 0 0 0 | uint64 LE index      (domain 1 = record, 2 = chunk)
/// aad       = header | domain | index | final flag      (final flag stops truncation; index stops reordering)
/// </summary>
public static class ObjectFormat
{
    public const string EntryExtension = ".ssentry";
    public const string BlobExtension = ".ssblob";
    public const int HeaderSize = 64;
    public const int DefaultChunkSize = 1 << 20;
    private const int TagSize = 16;
    private const byte FormatVersion = 2;
    private const byte KindEntry = 1;
    private const byte KindBlob = 2;
    private const byte DomainRecord = 1;
    private const byte DomainChunk = 2;
    private const int RecordPadding = 256;
    private const int MaxRecordLength = 64 * 1024;
    private const int MinChunkSize = 4 * 1024;
    private const int MaxChunkSize = 16 * 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "SSYNCOB2"u8;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ---- Entries ---------------------------------------------------------------------------------

    public static byte[] EncryptEntry(ReadOnlySpan<byte> fileKey, EntryRecord record)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(record, Json);
        if (json.Length > MaxRecordLength - 4 - RecordPadding) throw new VaultException("Name is too long.");
        var header = BuildHeader(KindEntry, record.Id, DefaultChunkSize);
        using var aes = CreateCipher(fileKey, header);

        int plainLength = (4 + json.Length + RecordPadding - 1) / RecordPadding * RecordPadding;
        var plain = new byte[plainLength];
        BinaryPrimitives.WriteInt32LittleEndian(plain, json.Length);
        json.CopyTo(plain, 4);

        var output = new byte[HeaderSize + 4 + plainLength + TagSize];
        header.CopyTo(output, 0);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(HeaderSize), plainLength + TagSize);
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> aad = stackalloc byte[HeaderSize + 10];
        BuildNonceAndAad(header, DomainRecord, 0, true, nonce, aad);
        aes.Encrypt(nonce, plain, output.AsSpan(HeaderSize + 4, plainLength), output.AsSpan(HeaderSize + 4 + plainLength, TagSize), aad);
        return output;
    }

    /// <summary>Authenticates and decrypts an entry. Throws <see cref="ObjectCorruptException"/> if anything is wrong.</summary>
    public static EntryRecord DecryptEntry(ReadOnlySpan<byte> fileKey, ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize + 4 + TagSize) throw new ObjectCorruptException("Record is truncated.");
        var header = data[..HeaderSize].ToArray();
        ParseHeader(header, KindEntry, out var id, out _);
        int length = BinaryPrimitives.ReadInt32LittleEndian(data[HeaderSize..]);
        if (length < TagSize + 4 || length > MaxRecordLength + TagSize || HeaderSize + 4 + length != data.Length)
            throw new ObjectCorruptException("Invalid record length.");
        int plainLength = length - TagSize;
        var plain = new byte[plainLength];
        using var aes = CreateCipher(fileKey, header);
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> aad = stackalloc byte[HeaderSize + 10];
        BuildNonceAndAad(header, DomainRecord, 0, true, nonce, aad);
        try
        {
            aes.Decrypt(nonce, data.Slice(HeaderSize + 4, plainLength), data.Slice(HeaderSize + 4 + plainLength, TagSize), plain, aad);
        }
        catch (CryptographicException e)
        {
            throw new ObjectCorruptException("Could not decrypt (different chain key, damaged or tampered with).", e);
        }

        int jsonLength = BinaryPrimitives.ReadInt32LittleEndian(plain);
        if (jsonLength < 0 || jsonLength > plainLength - 4) throw new ObjectCorruptException("Invalid record.");
        EntryRecord? record;
        try
        {
            record = JsonSerializer.Deserialize<EntryRecord>(plain.AsSpan(4, jsonLength), Json);
        }
        catch (JsonException e)
        {
            throw new ObjectCorruptException("Invalid record.", e);
        }
        if (record is null || record.Id != id || record.Kind is not (EntryKind.File or EntryKind.Folder))
            throw new ObjectCorruptException("Invalid record.");
        return record;
    }

    // ---- Blobs -----------------------------------------------------------------------------------

    public static void EncryptBlob(Stream output, ReadOnlySpan<byte> fileKey, ObjectId id, Stream plaintext,
        Action<long>? progress = null, CancellationToken ct = default, int chunkSize = DefaultChunkSize)
    {
        var header = BuildHeader(KindBlob, id, chunkSize);
        using var aes = CreateCipher(fileKey, header);
        output.Write(header);

        var current = new byte[chunkSize];
        var next = new byte[chunkSize];
        var cipher = new byte[chunkSize];
        int currentLength = ReadFull(plaintext, current);
        for (ulong index = 0; ; index++)
        {
            ct.ThrowIfCancellationRequested();
            int nextLength = currentLength == chunkSize ? ReadFull(plaintext, next) : 0;
            bool final = nextLength == 0;
            WriteChunk(output, aes, header, index, final, current.AsSpan(0, currentLength), cipher);
            progress?.Invoke(currentLength);
            if (final) break;
            (current, next) = (next, current);
            currentLength = nextLength;
        }
        CryptographicOperations.ZeroMemory(current);
        CryptographicOperations.ZeroMemory(next);
    }

    /// <summary>
    /// Authenticates an entire blob and returns its id. When <paramref name="output"/> is non-null the decrypted
    /// content is written to it. Throws <see cref="ObjectCorruptException"/> if anything was modified or truncated.
    /// </summary>
    public static ObjectId DecryptBlob(Stream input, ReadOnlySpan<byte> fileKey, Stream? output = null,
        Action<long>? progress = null, CancellationToken ct = default)
    {
        var header = new byte[HeaderSize];
        ReadExact(input, header);
        ParseHeader(header, KindBlob, out var id, out int chunkSize);
        using var aes = CreateCipher(fileKey, header);

        uint? length = TryReadUInt32(input) ?? throw new ObjectCorruptException("Encrypted file is truncated.");
        var cipher = new byte[chunkSize + TagSize];
        var plain = new byte[chunkSize];
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> aad = stackalloc byte[HeaderSize + 10];
        try
        {
            for (ulong index = 0; ; index++)
            {
                ct.ThrowIfCancellationRequested();
                uint chunkLength = length!.Value;
                if (chunkLength < TagSize || chunkLength > chunkSize + TagSize)
                    throw new ObjectCorruptException("Invalid chunk length.");
                int cipherLength = (int)chunkLength - TagSize;
                ReadExact(input, cipher.AsSpan(0, (int)chunkLength));
                uint? nextLength = TryReadUInt32(input);
                bool final = nextLength is null;
                if (!final && cipherLength != chunkSize)
                    throw new ObjectCorruptException("Short chunk in the middle of the file.");

                BuildNonceAndAad(header, DomainChunk, index, final, nonce, aad);
                try
                {
                    aes.Decrypt(nonce, cipher.AsSpan(0, cipherLength), cipher.AsSpan(cipherLength, TagSize),
                        plain.AsSpan(0, cipherLength), aad);
                }
                catch (CryptographicException e)
                {
                    throw new ObjectCorruptException("Encrypted file failed verification (damaged or tampered with).", e);
                }
                output?.Write(plain, 0, cipherLength);
                progress?.Invoke(cipherLength);
                if (final) break;
                length = nextLength;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
        return id;
    }

    // ---- Shared ----------------------------------------------------------------------------------

    private static byte[] BuildHeader(byte kind, ObjectId id, int chunkSize)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[8] = FormatVersion;
        header[9] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)chunkSize);
        id.Write(header.AsSpan(16, ObjectId.Size));
        RandomNumberGenerator.Fill(header.AsSpan(32, 32));
        return header;
    }

    private static void ParseHeader(byte[] header, byte expectedKind, out ObjectId id, out int chunkSize)
    {
        if (!header.AsSpan(0, 8).SequenceEqual(Magic) || header[8] != FormatVersion)
            throw new ObjectCorruptException("Not a SecureSync object (bad header).");
        if (header[9] != expectedKind) throw new ObjectCorruptException("Unexpected object type.");
        chunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (chunkSize is < MinChunkSize or > MaxChunkSize) throw new ObjectCorruptException("Invalid chunk size.");
        id = ObjectId.Read(header.AsSpan(16, ObjectId.Size));
    }

    private static AesGcm CreateCipher(ReadOnlySpan<byte> fileKey, byte[] header)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, fileKey.ToArray(), 32,
            header.AsSpan(32, 32).ToArray(), [.. "SecureSync/v2/object"u8, .. header]);
        try { return new AesGcm(key, TagSize); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static void WriteChunk(Stream output, AesGcm aes, byte[] header, ulong index, bool final,
        ReadOnlySpan<byte> plain, byte[] cipher)
    {
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> aad = stackalloc byte[HeaderSize + 10];
        Span<byte> tag = stackalloc byte[TagSize];
        Span<byte> length = stackalloc byte[4];
        BuildNonceAndAad(header, DomainChunk, index, final, nonce, aad);
        aes.Encrypt(nonce, plain, cipher.AsSpan(0, plain.Length), tag, aad);
        BinaryPrimitives.WriteInt32LittleEndian(length, plain.Length + TagSize);
        output.Write(length);
        output.Write(cipher, 0, plain.Length);
        output.Write(tag);
    }

    private static void BuildNonceAndAad(byte[] header, byte domain, ulong index, bool final, Span<byte> nonce, Span<byte> aad)
    {
        nonce.Clear();
        nonce[0] = domain;
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], index);
        header.CopyTo(aad);
        aad[HeaderSize] = domain;
        BinaryPrimitives.WriteUInt64LittleEndian(aad.Slice(HeaderSize + 1, 8), index);
        aad[HeaderSize + 9] = final ? (byte)1 : (byte)0;
    }

    private static int ReadFull(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        try { stream.ReadExactly(buffer); }
        catch (EndOfStreamException e) { throw new ObjectCorruptException("Encrypted file is truncated.", e); }
    }

    /// <summary>Returns null at a clean end of stream; throws if the stream ends part-way through the integer.</summary>
    private static uint? TryReadUInt32(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4];
        int read = stream.ReadAtLeast(buffer, 4, throwOnEndOfStream: false);
        if (read == 0) return null;
        if (read < 4) throw new ObjectCorruptException("Encrypted file is truncated.");
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }
}
