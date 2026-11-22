using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SecureSync.Core.Storage;

public enum EntryKind : byte
{
    File = 1,
    Folder = 2,
}

/// <summary>Random 128-bit identifier of a file, folder or content blob.</summary>
[JsonConverter(typeof(ObjectIdJsonConverter))]
public readonly struct ObjectId : IEquatable<ObjectId>, IComparable<ObjectId>
{
    public const int Size = 16;
    private readonly UInt128 _value;

    private ObjectId(UInt128 value) => _value = value;

    public static ObjectId New()
    {
        Span<byte> bytes = stackalloc byte[Size];
        RandomNumberGenerator.Fill(bytes);
        return Read(bytes);
    }

    public static ObjectId Read(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadUInt128BigEndian(bytes));

    public void Write(Span<byte> destination) => BinaryPrimitives.WriteUInt128BigEndian(destination, _value);

    public static bool TryParse(string? hex, out ObjectId id)
    {
        id = default;
        if (hex is null || hex.Length != 32) return false;
        if (!UInt128.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value)) return false;
        id = new ObjectId(value);
        return true;
    }

    public override string ToString() => _value.ToString("x32", CultureInfo.InvariantCulture);
    public bool Equals(ObjectId other) => _value == other._value;
    public override bool Equals(object? obj) => obj is ObjectId other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public int CompareTo(ObjectId other) => _value.CompareTo(other._value);
    public static bool operator ==(ObjectId a, ObjectId b) => a.Equals(b);
    public static bool operator !=(ObjectId a, ObjectId b) => !a.Equals(b);
}

internal sealed class ObjectIdJsonConverter : JsonConverter<ObjectId>
{
    public override ObjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ObjectId.TryParse(reader.GetString(), out var id) ? id : throw new JsonException("Bad object id.");

    public override void Write(Utf8JsonWriter writer, ObjectId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

/// <summary>
/// Version of an entry: a Lamport clock plus the id of the device that made the change. Every change gets a stamp
/// greater than any the device has seen, so when two devices changed the same item while apart, all devices agree
/// on which change is newer (higher counter; ties broken by device id) and keep that one.
/// </summary>
[JsonConverter(typeof(StampJsonConverter))]
public readonly record struct Stamp(ulong Counter, UInt128 Device) : IComparable<Stamp>
{
    public const int Size = 8 + 16;

    public int CompareTo(Stamp other) => Counter != other.Counter ? Counter.CompareTo(other.Counter) : Device.CompareTo(other.Device);
    public static bool operator >(Stamp a, Stamp b) => a.CompareTo(b) > 0;
    public static bool operator <(Stamp a, Stamp b) => a.CompareTo(b) < 0;
    public static bool operator >=(Stamp a, Stamp b) => a.CompareTo(b) >= 0;
    public static bool operator <=(Stamp a, Stamp b) => a.CompareTo(b) <= 0;

    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, Counter);
        BinaryPrimitives.WriteUInt128BigEndian(destination[8..], Device);
    }

    public static Stamp Read(ReadOnlySpan<byte> source) =>
        new(BinaryPrimitives.ReadUInt64LittleEndian(source), BinaryPrimitives.ReadUInt128BigEndian(source[8..]));
}

internal sealed class StampJsonConverter : JsonConverter<Stamp>
{
    public override Stamp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var parts = (reader.GetString() ?? "").Split('.');
        if (parts.Length != 2
            || !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var counter)
            || !UInt128.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var device))
            throw new JsonException("Bad stamp.");
        return new Stamp(counter, device);
    }

    public override void Write(Utf8JsonWriter writer, Stamp value, JsonSerializerOptions options) =>
        writer.WriteStringValue($"{value.Counter.ToString(CultureInfo.InvariantCulture)}.{value.Device.ToString("x32", CultureInfo.InvariantCulture)}");
}

/// <summary>
/// One file or folder, as stored (encrypted) in an entry object. Entries are never removed: a deleted item keeps a
/// record with <see cref="Deleted"/> set, so the deletion can sync to devices that were offline.
/// File contents live in a separate, immutable blob; replacing a file's contents points the entry at a new blob.
/// </summary>
public sealed record EntryRecord
{
    public required ObjectId Id { get; init; }
    public required EntryKind Kind { get; init; }
    public required string Name { get; init; }
    /// <summary>Containing folder; null for the top level.</summary>
    public ObjectId? Parent { get; init; }
    public bool Deleted { get; init; }
    public ObjectId? Blob { get; init; }
    public long Size { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string CreatedBy { get; init; } = "";
    public DateTime ChangedUtc { get; init; }
    public string ChangedBy { get; init; } = "";
    public required Stamp Stamp { get; init; }
}

/// <summary>A live file or folder as the UI sees it, with conflicts and orphans already resolved.</summary>
public sealed class VaultItem
{
    internal VaultItem(EntryRecord record, ObjectId? parent, bool contentAvailable)
    {
        Record = record;
        Parent = parent;
        IsAvailable = contentAvailable;
    }

    internal EntryRecord Record { get; }
    public ObjectId Id => Record.Id;
    public EntryKind Kind => Record.Kind;
    public bool IsFolder => Record.Kind == EntryKind.Folder;
    public string Name => Record.Name;
    /// <summary>Effective containing folder (null = top level).</summary>
    public ObjectId? Parent { get; }
    public ObjectId? Blob => Record.Blob;
    public long Size => Record.Size;
    public DateTime ModifiedUtc => Record.ModifiedUtc;
    public DateTime CreatedUtc => Record.CreatedUtc;
    public string CreatedBy => Record.CreatedBy;
    public DateTime ChangedUtc => Record.ChangedUtc;
    public string ChangedBy => Record.ChangedBy;
    /// <summary>For files: whether the encrypted contents are on this computer yet. Always true for folders.</summary>
    public bool IsAvailable { get; }
}

public enum VaultChangeKind { Entry, Blob }

public sealed record VaultChange(VaultChangeKind Kind, ObjectId Id, bool IsRemote, object? Source);

public class VaultException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class ObjectCorruptException(string message, Exception? inner = null) : VaultException(message, inner);

public sealed class ContentUnavailableException()
    : VaultException("This file hasn't finished syncing to this computer yet.");
