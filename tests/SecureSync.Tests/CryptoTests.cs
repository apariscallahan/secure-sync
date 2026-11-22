using System.Security.Cryptography;
using System.Text;
using SecureSync.Core.Crypto;
using SecureSync.Core.Storage;

namespace SecureSync.Tests;

public class ChainKeyTests
{
    [Fact]
    public void Keys_look_like_bitcoin_private_keys_and_round_trip()
    {
        for (int i = 0; i < 300; i++)
        {
            using var key = ChainKey.Generate();
            var text = key.Reveal();
            Assert.Equal(ChainKey.TextLength, text.Length);
            Assert.StartsWith("SS", text);
            Assert.True(ChainKey.TryParse(text, out var parsed));
            Assert.True(parsed!.SameChainAs(key));
            Assert.Equal(key.Fingerprint, parsed.Fingerprint);
        }
        // Extremes of the 256-bit range still give 52 characters starting with "SS".
        foreach (var fill in new byte[] { 0x00, 0xFF })
        {
            using var key = ChainKey.FromSecret(Enumerable.Repeat(fill, 32).ToArray());
            Assert.Equal(52, key.Reveal().Length);
            Assert.StartsWith("SS", key.Reveal());
        }
    }

    [Fact]
    public void Spaces_dashes_and_line_breaks_are_ignored()
    {
        using var key = ChainKey.Generate();
        var text = key.Reveal();
        var spaced = string.Join(" ", text.Chunk(4).Select(c => new string(c)));
        Assert.True(ChainKey.TryParse(spaced, out _));
        Assert.True(ChainKey.TryParse(text[..26] + "-\r\n" + text[26..], out _));
    }

    [Fact]
    public void Every_single_character_typo_is_detected()
    {
        using var key = ChainKey.Generate();
        var text = key.Reveal();
        for (int i = 0; i < text.Length; i++)
        {
            var chars = text.ToCharArray();
            chars[i] = chars[i] == 'a' ? 'b' : 'a';
            Assert.False(ChainKey.TryParse(new string(chars), out _), $"typo at {i} not detected");
        }
        Assert.False(ChainKey.TryParse(text[..^1], out _));
    }

    [Fact]
    public void Fingerprint_identifies_the_chain_without_revealing_the_key()
    {
        using var a = ChainKey.Generate();
        using var b = ChainKey.Generate();
        Assert.Matches("^[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$", a.Fingerprint);
        Assert.NotEqual(a.Fingerprint, b.Fingerprint);
        Assert.DoesNotContain(a.Fingerprint.Replace("-", ""), Convert.ToHexString(a.Secret));
    }

    [Fact]
    public void Same_key_gives_same_working_keys_on_every_device()
    {
        using var key = ChainKey.Generate();
        Assert.True(ChainKey.TryParse(key.Reveal(), out var copy));
        using var a = new ChainKeys(key);
        using var b = new ChainKeys(copy!);
        using var other = new ChainKeys(ChainKey.Generate());
        Assert.True(a.FileKey.SequenceEqual(b.FileKey));
        Assert.True(a.AuthKey.SequenceEqual(b.AuthKey));
        Assert.Equal(a.DiscoveryTag, b.DiscoveryTag);
        Assert.False(a.FileKey.SequenceEqual(other.FileKey));
        Assert.False(a.FileKey.SequenceEqual(a.AuthKey));
    }
}

public class ObjectFormatTests
{
    private const int Chunk = 4096;
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    private static byte[] EncryptBlob(byte[] content, ObjectId? id = null)
    {
        using var output = new MemoryStream();
        ObjectFormat.EncryptBlob(output, Key, id ?? ObjectId.New(), new MemoryStream(content), chunkSize: Chunk);
        return output.ToArray();
    }

    private static byte[] DecryptBlob(byte[] blob, byte[]? key = null)
    {
        using var output = new MemoryStream();
        ObjectFormat.DecryptBlob(new MemoryStream(blob), key ?? Key, output);
        return output.ToArray();
    }

    private static EntryRecord Record(string name = "Tax return 2025.pdf") => new()
    {
        Id = ObjectId.New(), Kind = EntryKind.File, Name = name, Parent = ObjectId.New(), Blob = ObjectId.New(), Size = 123,
        CreatedBy = "PC", ChangedBy = "PC", Stamp = new Stamp(7, 42),
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(3 * Chunk + 17)]
    public void Blobs_round_trip(int size)
    {
        var content = RandomNumberGenerator.GetBytes(size);
        var id = ObjectId.New();
        var blob = EncryptBlob(content, id);
        Assert.Equal(content, DecryptBlob(blob));
        Assert.Equal(id, ObjectFormat.DecryptBlob(new MemoryStream(blob), Key));
    }

    [Fact]
    public void Any_flipped_bit_in_a_blob_is_detected()
    {
        var blob = EncryptBlob(RandomNumberGenerator.GetBytes(2 * Chunk + 100));
        for (int i = 0; i < blob.Length; i += 37)
        {
            var tampered = (byte[])blob.Clone();
            tampered[i] ^= 0x01;
            Assert.ThrowsAny<VaultException>(() => DecryptBlob(tampered));
        }
    }

    [Fact]
    public void Blob_truncation_and_appended_data_are_detected()
    {
        var blob = EncryptBlob(RandomNumberGenerator.GetBytes(3 * Chunk));
        Assert.Throws<ObjectCorruptException>(() => DecryptBlob(blob[..^(4 + Chunk + 16)]));
        Assert.Throws<ObjectCorruptException>(() => DecryptBlob(blob[..^1]));
        Assert.Throws<ObjectCorruptException>(() => DecryptBlob([.. blob, 0, 0, 0, 0]));
        Assert.Throws<ObjectCorruptException>(() => DecryptBlob(blob, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void Entries_round_trip_and_hide_names()
    {
        var record = Record("Secret plans.docx");
        var bytes = ObjectFormat.EncryptEntry(Key, record);
        Assert.Equal(record, ObjectFormat.DecryptEntry(Key, bytes));
        Assert.DoesNotContain("Secret", Encoding.UTF8.GetString(bytes));
        // Name length is hidden by padding.
        Assert.Equal(bytes.Length, ObjectFormat.EncryptEntry(Key, Record("a")).Length);
    }

    [Fact]
    public void Tampered_or_foreign_entries_are_rejected()
    {
        var bytes = ObjectFormat.EncryptEntry(Key, Record());
        for (int i = 0; i < bytes.Length; i += 11)
        {
            var tampered = (byte[])bytes.Clone();
            tampered[i] ^= 0x01;
            Assert.ThrowsAny<VaultException>(() => ObjectFormat.DecryptEntry(Key, tampered));
        }
        Assert.Throws<ObjectCorruptException>(() => ObjectFormat.DecryptEntry(RandomNumberGenerator.GetBytes(32), bytes));
    }

    [Fact]
    public void Stamps_order_by_counter_then_device()
    {
        Assert.True(new Stamp(2, 1) > new Stamp(1, 99));
        Assert.True(new Stamp(2, 5) > new Stamp(2, 4));
        Span<byte> buffer = stackalloc byte[Stamp.Size];
        var stamp = new Stamp(123456789, UInt128.MaxValue - 5);
        stamp.Write(buffer);
        Assert.Equal(stamp, Stamp.Read(buffer));
    }
}

public class PathSafetyTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("what?")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    public void Invalid_names_are_rejected(string name) => Assert.NotNull(PathSafety.ValidateName(name));

    [Theory]
    [InlineData("Report.pdf")]
    [InlineData("Iceland trip 2026")]
    [InlineData(".gitignore")]
    [InlineData("Café — notes (draft)")]
    public void Normal_names_are_accepted(string name) => Assert.Null(PathSafety.ValidateName(name));

    [Theory]
    [InlineData(new[] { "..", ".." }, "evil.txt")]
    [InlineData(new[] { "C:\\Windows" }, "x")]
    [InlineData(new string[0], "..\\..\\x")]
    [InlineData(new string[0], "CON")]
    public void Names_from_records_cannot_escape_the_destination(string[] folders, string name)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "securesync-pathtest"));
        var path = PathSafety.ToSafeLocalPath(root, folders, name);
        Assert.StartsWith(root + Path.DirectorySeparatorChar, path);
        var segments = path[(root.Length + 1)..].Split(Path.DirectorySeparatorChar);
        Assert.All(segments, s => Assert.False(s is "." or ".." or "" || s.Contains(':'), $"unsafe segment '{s}'"));
    }

    [Fact]
    public void Unique_names_get_a_counter()
    {
        var taken = new HashSet<string> { "a.txt", "a (2).txt", "Photos" };
        Assert.Equal("a (3).txt", PathSafety.MakeUnique("a.txt", taken.Contains));
        Assert.Equal("Photos (2)", PathSafety.MakeUnique("Photos", taken.Contains, isFolder: true));
    }
}
