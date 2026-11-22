using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using SecureSync.Core.Crypto;

namespace SecureSync.Core.Storage;

/// <summary>
/// The encrypted store inside the folder the user picked:
///
///   securesync.chain          chain fingerprint (identifies the chain; not secret, reveals nothing)
///   securesync.device         this computer's random ID
///   objects/entries/*.ssentry one encrypted record per file or folder (name, folder, dates, version)
///   objects/blobs/*.ssblob    encrypted file contents
///
/// Everything is named by random IDs. Without the chain key, the folder reveals nothing but rough sizes.
///
/// Entries carry a version <see cref="Stamp"/>. A change (create, rename, move, delete, replace) writes a new
/// version; when devices meet, each keeps the newest version of every entry, so changes made while apart merge
/// automatically. Blobs are immutable and are deleted once no live entry refers to them.
/// </summary>
public sealed class Vault : IDisposable
{
    public const string ChainFileName = "securesync.chain";
    private const string DeviceFileName = "securesync.device";
    private const string LockFileName = "securesync.lock";
    private const int FolderFormat = 2;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly object _gate = new();
    private readonly Dictionary<ObjectId, EntryRecord> _entries = [];
    private readonly HashSet<ObjectId> _blobs = [];
    private readonly Dictionary<ObjectId, int> _blobRefs = [];
    private readonly Dictionary<ObjectId, long> _missing = []; // contents live entries need but this device lacks
    private readonly FileStream _lock;
    private readonly string _tempRoot;
    private readonly FileStream _tempLock;
    private readonly UInt128 _device;
    private ulong _clock;
    private VaultSnapshot? _snapshot;
    private bool _disposed;

    private sealed record ChainFile(string App, int Format, string? Fingerprint, DateTime CreatedUtc);
    private sealed record DeviceFile(string DeviceId);

    private Vault(string folder, ChainKeys keys, string deviceName, FileStream lockStream)
    {
        Folder = folder;
        Keys = keys;
        DeviceName = deviceName;
        _lock = lockStream;
        DeviceId = LoadOrCreateDeviceId(folder);
        _device = BinaryPrimitives.ReadUInt128BigEndian(DeviceId);
        (_tempRoot, _tempLock) = CreateTempRoot();
    }

    public string Folder { get; }
    public string Fingerprint => Keys.Fingerprint;
    public string DeviceName { get; }
    public byte[] DeviceId { get; }
    internal ChainKeys Keys { get; }

    /// <summary>Records that could not be read while opening (damaged). They are replaced by the next sync.</summary>
    public int UnreadableObjects { get; private set; }

    /// <summary>Raised on any thread after a file or folder changes, locally or because of a sync.</summary>
    public event EventHandler<VaultChange>? Changed;

    private string EntriesDir => Path.Combine(Folder, "objects", "entries");
    private string BlobsDir => Path.Combine(Folder, "objects", "blobs");
    private string IncomingDir => Path.Combine(Folder, "objects", ".incoming");
    private string EntryPath(ObjectId id) => Path.Combine(EntriesDir, id + ObjectFormat.EntryExtension);
    private string BlobPath(ObjectId id) => Path.Combine(BlobsDir, id + ObjectFormat.BlobExtension);

    // ---- Opening ---------------------------------------------------------------------------------

    public static Vault Open(string folder, ChainKey key, string deviceName)
    {
        var keys = new ChainKeys(key);
        try
        {
            return Open(folder, keys, deviceName);
        }
        catch
        {
            keys.Dispose();
            throw;
        }
    }

    private static Vault Open(string folder, ChainKeys keys, string deviceName)
    {
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);

        FileStream lockStream;
        try
        {
            lockStream = new FileStream(Path.Combine(folder, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException e)
        {
            throw new VaultException("This folder is already open in another SecureSync window.", e);
        }

        try
        {
            var chainPath = Path.Combine(folder, ChainFileName);
            var stored = ReadChainFile(folder);
            if (stored is not null)
            {
                if (stored.Format != FolderFormat)
                    throw new VaultException("This folder was made by an earlier test version of SecureSync. Choose a new, empty folder.");
                if (!string.Equals(stored.Fingerprint, keys.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new VaultException($"This folder belongs to a different sync chain ({stored.Fingerprint}). Choose an empty folder, or the folder you used for this chain.");
            }
            else
            {
                File.WriteAllText(chainPath, JsonSerializer.Serialize(new ChainFile("SecureSync", FolderFormat, keys.Fingerprint, DateTime.UtcNow), Json));
            }

            var vault = new Vault(folder, keys, deviceName, lockStream);
            vault.Scan();
            return vault;
        }
        catch
        {
            lockStream.Dispose();
            throw;
        }
    }

    /// <summary>Returns the fingerprint of the chain a folder belongs to, or null if it isn't a vault folder.</summary>
    public static string? ReadFingerprint(string folder) => ReadChainFile(folder)?.Fingerprint;

    private static ChainFile? ReadChainFile(string folder)
    {
        try
        {
            var path = Path.Combine(folder, ChainFileName);
            if (!File.Exists(path)) return null;
            var file = JsonSerializer.Deserialize<ChainFile>(File.ReadAllText(path), Json);
            return file is null ? null : file with { Format = file.Fingerprint is null ? 1 : file.Format };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new VaultException("The sync chain file in this folder can't be read.", e);
        }
    }

    private static byte[] LoadOrCreateDeviceId(string folder)
    {
        var path = Path.Combine(folder, DeviceFileName);
        try
        {
            if (File.Exists(path))
            {
                var stored = JsonSerializer.Deserialize<DeviceFile>(File.ReadAllText(path), Json);
                if (stored is not null && stored.DeviceId.Length == 32)
                    return Convert.FromHexString(stored.DeviceId);
            }
        }
        catch (Exception e) when (e is JsonException or FormatException) { /* regenerate */ }
        var id = RandomNumberGenerator.GetBytes(16);
        File.WriteAllText(path, JsonSerializer.Serialize(new DeviceFile(Convert.ToHexStringLower(id)), Json));
        return id;
    }

    private void Scan()
    {
        Directory.CreateDirectory(EntriesDir);
        Directory.CreateDirectory(BlobsDir);
        if (Directory.Exists(IncomingDir)) Directory.Delete(IncomingDir, recursive: true);
        Directory.CreateDirectory(IncomingDir);
        foreach (var dir in new[] { EntriesDir, BlobsDir })
            foreach (var leftover in Directory.EnumerateFiles(dir, "*.old")) TryDelete(leftover);

        int unreadable = 0;
        foreach (var path in Directory.EnumerateFiles(EntriesDir, "*" + ObjectFormat.EntryExtension))
        {
            if (!ObjectId.TryParse(Path.GetFileNameWithoutExtension(path), out var id)) continue;
            try
            {
                var record = ObjectFormat.DecryptEntry(Keys.FileKey, File.ReadAllBytes(path));
                if (record.Id != id) { unreadable++; continue; }
                _entries[id] = record;
                _clock = Math.Max(_clock, record.Stamp.Counter);
            }
            catch (Exception e) when (e is ObjectCorruptException or IOException)
            {
                unreadable++; // missing from _entries, so the next sync fetches a good copy and overwrites it
            }
        }
        foreach (var path in Directory.EnumerateFiles(BlobsDir, "*" + ObjectFormat.BlobExtension))
            if (ObjectId.TryParse(Path.GetFileNameWithoutExtension(path), out var id)) _blobs.Add(id);

        foreach (var record in _entries.Values) AddRef(record);
        foreach (var present in _blobs) _missing.Remove(present);
        foreach (var orphan in _blobs.Where(b => !_blobRefs.ContainsKey(b)).ToList())
        {
            TryDelete(BlobPath(orphan));
            _blobs.Remove(orphan);
        }
        UnreadableObjects = unreadable;
    }

    // ---- Reading ---------------------------------------------------------------------------------

    /// <summary>An immutable view of every live file and folder. Cheap to call repeatedly; rebuilt after changes.</summary>
    public VaultSnapshot Snapshot()
    {
        lock (_gate) return SnapshotLocked();
    }

    private VaultSnapshot SnapshotLocked() => _snapshot ??= VaultSnapshot.Build(_entries, _blobs);

    // ---- Local changes ---------------------------------------------------------------------------

    public VaultItem CreateFolder(ObjectId? parent, string name)
    {
        ThrowIfInvalidName(name);
        ObjectId id;
        lock (_gate)
        {
            var snap = SnapshotLocked();
            EnsureFolder(snap, parent);
            EnsureNameFree(snap, parent, name, except: null);
            var now = DateTime.UtcNow;
            id = ObjectId.New();
            StoreLocked(new EntryRecord
            {
                Id = id, Kind = EntryKind.Folder, Name = name, Parent = parent,
                CreatedUtc = now, ModifiedUtc = now, ChangedUtc = now, CreatedBy = DeviceName, ChangedBy = DeviceName,
                Stamp = NextStamp(),
            });
        }
        Raise(VaultChangeKind.Entry, id);
        return Snapshot().Get(id)!;
    }

    /// <summary>
    /// Encrypts a file from disk into <paramref name="parent"/>. With <paramref name="replace"/>, the existing file's
    /// contents are replaced (keeping its identity, so the change syncs as an update).
    /// </summary>
    public VaultItem AddFile(string sourcePath, ObjectId? parent, string? name = null, ObjectId? replace = null,
        Action<long>? progress = null, CancellationToken ct = default)
    {
        var source = new FileInfo(sourcePath);
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1 << 16, FileOptions.SequentialScan);
        return AddFile(input, name ?? source.Name, source.LastWriteTimeUtc, parent, replace, progress, ct);
    }

    /// <summary>Encrypts content from a stream (e.g. a file picked on a phone). The stream is read to the end.</summary>
    public VaultItem AddFile(Stream content, string name, DateTime modifiedUtc, ObjectId? parent, ObjectId? replace = null,
        Action<long>? progress = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ThrowIfInvalidName(name);
        lock (_gate)
        {
            var snap = SnapshotLocked();
            EnsureFolder(snap, parent);
            if (replace is null) EnsureNameFree(snap, parent, name, except: null);
        }

        var blobId = ObjectId.New();
        var temp = NewIncomingPath();
        ObjectId id;
        try
        {
            long size = 0;
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
            {
                ObjectFormat.EncryptBlob(output, Keys.FileKey, blobId, content, n =>
                {
                    size += n;
                    progress?.Invoke(n);
                }, ct);
                output.Flush(flushToDisk: true);
            }

            lock (_gate)
            {
                var snap = SnapshotLocked();
                var now = DateTime.UtcNow;
                EntryRecord record;
                if (replace is { } replaceId && snap.Get(replaceId) is { IsFolder: false } existing)
                {
                    record = existing.Record with
                    {
                        Blob = blobId, Size = size, ModifiedUtc = modifiedUtc,
                        ChangedUtc = now, ChangedBy = DeviceName, Stamp = NextStamp(),
                    };
                }
                else
                {
                    EnsureFolder(snap, parent);
                    // Someone may have taken the name while we were encrypting; don't throw away the work.
                    var finalName = PathSafety.MakeUnique(name, n => snap.FindChild(parent, n) is not null);
                    record = new EntryRecord
                    {
                        Id = ObjectId.New(), Kind = EntryKind.File, Name = finalName, Parent = parent, Blob = blobId,
                        Size = size, ModifiedUtc = modifiedUtc, CreatedUtc = now, ChangedUtc = now,
                        CreatedBy = DeviceName, ChangedBy = DeviceName, Stamp = NextStamp(),
                    };
                }
                MoveIntoPlace(temp, BlobPath(blobId));
                _blobs.Add(blobId);
                _missing.Remove(blobId);
                StoreLocked(record);
                id = record.Id;
            }
        }
        finally
        {
            TryDelete(temp);
        }
        Raise(VaultChangeKind.Entry, id);
        return Snapshot().Get(id)!;
    }

    public void Rename(ObjectId id, string newName)
    {
        ThrowIfInvalidName(newName);
        lock (_gate)
        {
            var snap = SnapshotLocked();
            var item = snap.Get(id) ?? throw new VaultException("That item no longer exists.");
            if (item.Name == newName) return;
            EnsureNameFree(snap, item.Parent, newName, except: id);
            StoreLocked(item.Record with { Name = newName, ChangedUtc = DateTime.UtcNow, ChangedBy = DeviceName, Stamp = NextStamp() });
        }
        Raise(VaultChangeKind.Entry, id);
    }

    /// <summary>Moves files and folders into <paramref name="target"/> (null = top level). Clashing names get " (2)".</summary>
    public void Move(IEnumerable<ObjectId> ids, ObjectId? target)
    {
        var moved = new List<ObjectId>();
        lock (_gate)
        {
            var snap = SnapshotLocked();
            EnsureFolder(snap, target);
            var taken = new HashSet<string>(snap.Children(target).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids.Distinct())
            {
                if (snap.Get(id) is not { } item || item.Parent == target) continue;
                if (item.IsFolder && target is { } t && snap.IsSelfOrDescendant(t, item.Id))
                    throw new VaultException($"You can't move “{item.Name}” into itself.");
                var name = PathSafety.MakeUnique(item.Name, taken.Contains, item.IsFolder);
                taken.Add(name);
                StoreLocked(item.Record with { Parent = target, Name = name, ChangedUtc = DateTime.UtcNow, ChangedBy = DeviceName, Stamp = NextStamp() });
                moved.Add(id);
            }
        }
        foreach (var id in moved) Raise(VaultChangeKind.Entry, id);
    }

    /// <summary>Deletes files and folders (with everything inside) on every synced computer.</summary>
    public int Delete(IEnumerable<ObjectId> ids)
    {
        var deleted = new List<ObjectId>();
        lock (_gate)
        {
            var snap = SnapshotLocked();
            var all = new HashSet<ObjectId>();
            foreach (var id in ids)
            {
                if (snap.Get(id) is not { } item) continue;
                all.Add(id);
                if (item.IsFolder) foreach (var d in snap.Descendants(id)) all.Add(d.Id);
            }
            var now = DateTime.UtcNow;
            foreach (var id in all)
            {
                var item = snap.Get(id)!;
                StoreLocked(item.Record with
                {
                    Deleted = true, Blob = null, Size = 0, ChangedUtc = now, ChangedBy = DeviceName, Stamp = NextStamp(),
                });
                deleted.Add(id);
            }
        }
        foreach (var id in deleted) Raise(VaultChangeKind.Entry, id);
        return deleted.Count;
    }

    /// <summary>Decrypts a file into a stream (e.g. a document the user picked on a phone).</summary>
    public void Export(ObjectId id, Stream output, Action<long>? progress = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var item = Snapshot().Get(id) ?? throw new VaultException("That file is no longer in the sync chain.");
        if (item.IsFolder || item.Blob is not { } blob) throw new VaultException("That's a folder, not a file.");
        using var input = OpenBlob(blob) ?? throw new ContentUnavailableException();
        try
        {
            ObjectFormat.DecryptBlob(input, Keys.FileKey, output, progress, ct);
        }
        catch (ObjectCorruptException)
        {
            input.Dispose();
            DiscardDamagedBlob(blob);
            throw new VaultException("This file was damaged on this device. A good copy will be downloaded from another device when they sync.");
        }
    }

    /// <summary>Decrypts a file to <paramref name="destinationPath"/> (written to a temp name first).</summary>
    public void Export(ObjectId id, string destinationPath, Action<long>? progress = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var item = Snapshot().Get(id) ?? throw new VaultException("That file is no longer in the sync chain.");
        if (item.IsFolder || item.Blob is not { } blob) throw new VaultException("That's a folder, not a file.");
        var partial = destinationPath + ".securesync-partial";
        try
        {
            using (var input = OpenBlob(blob) ?? throw new ContentUnavailableException())
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                try
                {
                    ObjectFormat.DecryptBlob(input, Keys.FileKey, output, progress, ct);
                }
                catch (ObjectCorruptException)
                {
                    input.Dispose();
                    DiscardDamagedBlob(blob);
                    throw new VaultException("This file was damaged on this computer. A good copy will be downloaded from your other computer when they sync.");
                }
            }
            File.Move(partial, destinationPath, overwrite: true);
            try { File.SetLastWriteTimeUtc(destinationPath, item.ModifiedUtc); } catch (IOException) { }
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>Decrypts a file into a private temp folder (cleaned up when the vault is locked) and returns its path.</summary>
    public string DecryptToTemp(ObjectId id)
    {
        var item = Snapshot().Get(id) ?? throw new VaultException("That file is no longer in the sync chain.");
        if (item.Blob is not { } blob) throw new VaultException("That's a folder, not a file.");
        var dir = Path.Combine(_tempRoot, blob.ToString());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, PathSafety.SanitizeSegment(item.Name));
        if (!File.Exists(path)) Export(id, path);
        return path;
    }

    /// <summary>
    /// Fully decrypts every file stored on this computer. Damaged ones are removed so the sync downloads good
    /// copies from another computer. Returns (checked, damaged).
    /// </summary>
    public (int Checked, int Damaged) VerifyAll(Action<int, int>? progress = null, CancellationToken ct = default)
    {
        List<ObjectId> blobs;
        lock (_gate) blobs = [.. _blobs];
        int damaged = 0;
        for (int i = 0; i < blobs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(i, blobs.Count);
            using var input = OpenBlob(blobs[i]);
            if (input is null) continue;
            bool ok;
            try
            {
                ok = ObjectFormat.DecryptBlob(input, Keys.FileKey) == blobs[i];
            }
            catch (ObjectCorruptException)
            {
                ok = false;
            }
            if (ok) continue;
            input.Dispose();
            DiscardDamagedBlob(blobs[i]);
            damaged++;
        }
        progress?.Invoke(blobs.Count, blobs.Count);
        return (blobs.Count, damaged);
    }

    private void DiscardDamagedBlob(ObjectId blob)
    {
        lock (_gate)
        {
            if (!_blobs.Remove(blob)) return;
            TryDelete(BlobPath(blob));
            if (_blobRefs.ContainsKey(blob))
                _missing[blob] = _entries.Values.FirstOrDefault(e => !e.Deleted && e.Blob == blob)?.Size ?? 0;
            _snapshot = null;
        }
        Raise(VaultChangeKind.Blob, blob);
    }

    // ---- Used by the sync engine -----------------------------------------------------------------

    internal List<(ObjectId Id, Stamp Stamp)> GetEntryInventory()
    {
        lock (_gate) return _entries.Values.Select(e => (e.Id, e.Stamp)).ToList();
    }

    /// <summary>SHA-256 over every (id, version) in id order: equal on two devices exactly when they hold the same versions.</summary>
    internal byte[] InventoryDigest()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> item = stackalloc byte[ObjectId.Size + Stamp.Size];
        lock (_gate)
        {
            foreach (var entry in _entries.Values.OrderBy(e => e.Id))
            {
                entry.Id.Write(item);
                entry.Stamp.Write(item[ObjectId.Size..]);
                hash.AppendData(item);
            }
        }
        return hash.GetHashAndReset();
    }

    internal bool WantsEntry(ObjectId id, Stamp stamp)
    {
        lock (_gate) return !_entries.TryGetValue(id, out var current) || stamp > current.Stamp;
    }

    internal byte[]? ReadEntryBytes(ObjectId id)
    {
        lock (_gate)
        {
            if (!_entries.ContainsKey(id)) return null;
            try { return File.ReadAllBytes(EntryPath(id)); }
            catch (IOException) { return null; }
        }
    }

    /// <summary>Authenticates a record received from another device and keeps it if it's newer. Returns true if applied.</summary>
    internal bool ApplyEntry(byte[] bytes, object? source)
    {
        ThrowIfDisposed();
        var record = ObjectFormat.DecryptEntry(Keys.FileKey, bytes);
        lock (_gate)
        {
            if (_entries.TryGetValue(record.Id, out var current) && current.Stamp >= record.Stamp) return false;
            StoreLocked(record, bytes);
        }
        Raise(VaultChangeKind.Entry, record.Id, isRemote: true, source);
        return true;
    }

    /// <summary>Contents that live files need but this computer doesn't have yet.</summary>
    internal List<(ObjectId Blob, long Size)> GetMissingBlobs()
    {
        lock (_gate) return _missing.Select(m => (m.Key, m.Value)).ToList();
    }

    internal bool IsBlobNeeded(ObjectId blob)
    {
        lock (_gate) return _blobRefs.ContainsKey(blob) && !_blobs.Contains(blob);
    }

    /// <summary>Name of a live file using this content, for messages.</summary>
    public string? NameOfBlob(ObjectId blob)
    {
        lock (_gate) return _entries.Values.FirstOrDefault(e => !e.Deleted && e.Blob == blob)?.Name;
    }

    internal FileStream? OpenBlob(ObjectId blob)
    {
        lock (_gate)
        {
            if (!_blobs.Contains(blob)) return null;
            try { return OpenRead(BlobPath(blob)); }
            catch (FileNotFoundException) { return null; }
        }
    }

    internal long BlobFileSize(ObjectId blob)
    {
        lock (_gate)
        {
            if (!_blobs.Contains(blob)) return 0;
            try { return new FileInfo(BlobPath(blob)).Length; }
            catch (IOException) { return 0; }
        }
    }

    internal string NewIncomingPath() => Path.Combine(IncomingDir, Guid.NewGuid().ToString("N") + ".part");

    /// <summary>Fully authenticates received contents, then stores them. Returns false if they're no longer needed.</summary>
    internal bool ImportBlob(string tempPath, ObjectId expected, object? source)
    {
        ThrowIfDisposed();
        ObjectId id;
        using (var input = OpenRead(tempPath))
            id = ObjectFormat.DecryptBlob(input, Keys.FileKey);
        if (id != expected) throw new ObjectCorruptException("Received the wrong file.");

        lock (_gate)
        {
            if (_blobs.Contains(id) || !_blobRefs.ContainsKey(id))
            {
                TryDelete(tempPath);
                return false;
            }
            MoveIntoPlace(tempPath, BlobPath(id));
            _blobs.Add(id);
            _missing.Remove(id);
            _snapshot = null;
        }
        Raise(VaultChangeKind.Blob, id, isRemote: true, source);
        return true;
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    private Stamp NextStamp() => new(++_clock, _device);

    /// <summary>Caller holds _gate.</summary>
    private void StoreLocked(EntryRecord record, byte[]? bytes = null)
    {
        bytes ??= ObjectFormat.EncryptEntry(Keys.FileKey, record);
        var temp = NewIncomingPath();
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            MoveIntoPlace(temp, EntryPath(record.Id));
        }
        finally
        {
            TryDelete(temp);
        }
        _entries.TryGetValue(record.Id, out var previous);
        _entries[record.Id] = record;
        _clock = Math.Max(_clock, record.Stamp.Counter);
        AddRef(record);
        if (previous is not null) RemoveRef(previous);
        _snapshot = null;
    }

    private void AddRef(EntryRecord record)
    {
        if (record.Deleted || record.Blob is not { } blob) return;
        _blobRefs[blob] = _blobRefs.GetValueOrDefault(blob) + 1;
        if (!_blobs.Contains(blob)) _missing[blob] = record.Size;
    }

    private void RemoveRef(EntryRecord record)
    {
        if (record.Deleted || record.Blob is not { } blob || !_blobRefs.TryGetValue(blob, out var count)) return;
        if (count > 1)
        {
            _blobRefs[blob] = count - 1;
            return;
        }
        _blobRefs.Remove(blob);
        _missing.Remove(blob);
        if (_blobs.Remove(blob)) TryDelete(BlobPath(blob));
    }

    private static void ThrowIfInvalidName(string name)
    {
        if (PathSafety.ValidateName(name) is { } problem) throw new VaultException(problem);
    }

    private static void EnsureFolder(VaultSnapshot snap, ObjectId? folder)
    {
        if (folder is { } id && snap.Get(id) is not { IsFolder: true })
            throw new VaultException("That folder no longer exists.");
    }

    private static void EnsureNameFree(VaultSnapshot snap, ObjectId? folder, string name, ObjectId? except)
    {
        if (snap.FindChild(folder, name) is { } clash && clash.Id != except)
            throw new VaultException($"There's already {(clash.IsFolder ? "a folder" : "a file")} named “{clash.Name}” here.");
    }

    private void Raise(VaultChangeKind kind, ObjectId id, bool isRemote = false, object? source = null) =>
        Changed?.Invoke(this, new VaultChange(kind, id, isRemote, source));

    private static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);

    /// <summary>
    /// Atomic replace. If a reader (e.g. an in-progress send to a peer) holds the old file open, Windows refuses to
    /// overwrite it, but because readers open with FileShare.Delete the old file can be renamed aside first.
    /// </summary>
    private static void MoveIntoPlace(string source, string destination)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 3)
            {
                Thread.Sleep(30);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                var aside = $"{destination}.{Guid.NewGuid():N}.old";
                File.Move(destination, aside);
                File.Move(source, destination);
                TryDelete(aside); // if still open it is removed on the next open
                return;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Per-session folder under %TEMP% for files opened from the vault. Each holds a lock file so a later session can
    /// tell abandoned folders (e.g. after a crash) from ones still in use, and delete them.
    /// </summary>
    private static (string Root, FileStream Lock) CreateTempRoot()
    {
        var parent = Path.Combine(Path.GetTempPath(), "SecureSync");
        Directory.CreateDirectory(parent);
        foreach (var stale in Directory.EnumerateDirectories(parent))
        {
            try
            {
                using (new FileStream(Path.Combine(stale, ".session"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                Directory.Delete(stale, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        for (int attempt = 0; ; attempt++)
        {
            var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                var lockStream = new FileStream(Path.Combine(root, ".session"), FileMode.Create, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.DeleteOnClose);
                return (root, lockStream);
            }
            catch (DirectoryNotFoundException) when (attempt < 3)
            {
                // Another SecureSync window cleaned up the folder in the instant before we locked it; try a new one.
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tempLock.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a file is still open in another app */ }
        Keys.Dispose();
        _lock.Dispose();
    }
}
