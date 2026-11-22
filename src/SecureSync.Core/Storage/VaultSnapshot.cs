namespace SecureSync.Core.Storage;

/// <summary>
/// Immutable tree of live files and folders, built from the latest entry versions.
///
/// Changes made on different computers while they were apart can leave odd shapes, which are resolved the same
/// way on every device:
///   • an item whose folder was deleted appears in the nearest folder that still exists;
///   • an item whose folder hasn't synced yet appears at the top level until it arrives;
///   • two folders moved into each other (a cycle) — the one with the smaller id is placed at the top level.
/// </summary>
public sealed class VaultSnapshot
{
    private static readonly IReadOnlyList<VaultItem> None = [];
    private readonly Dictionary<ObjectId, VaultItem> _byId;
    private readonly Dictionary<ObjectId, List<VaultItem>> _children = [];
    private readonly List<VaultItem> _top = [];

    private VaultSnapshot(List<VaultItem> items)
    {
        Items = items;
        _byId = items.ToDictionary(i => i.Id);
        foreach (var item in items)
        {
            if (item.Parent is { } p)
            {
                if (!_children.TryGetValue(p, out var list)) _children[p] = list = [];
                list.Add(item);
            }
            else
            {
                _top.Add(item);
            }
        }
    }

    public IReadOnlyList<VaultItem> Items { get; }
    public int FileCount => Items.Count(i => !i.IsFolder);
    public long TotalBytes => Items.Where(i => !i.IsFolder).Sum(i => i.Size);

    public VaultItem? Get(ObjectId id) => _byId.GetValueOrDefault(id);

    public IReadOnlyList<VaultItem> Children(ObjectId? folder) =>
        folder is { } id ? _children.TryGetValue(id, out var list) ? list : None : _top;

    public VaultItem? FindChild(ObjectId? folder, string name) =>
        Children(folder).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<VaultItem> Descendants(ObjectId folder)
    {
        var stack = new Stack<VaultItem>(Children(folder));
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            yield return item;
            if (item.IsFolder) foreach (var child in Children(item.Id)) stack.Push(child);
        }
    }

    /// <summary>Folders from the top level down to and including <paramref name="folder"/>.</summary>
    public IReadOnlyList<VaultItem> PathTo(ObjectId? folder)
    {
        var path = new List<VaultItem>();
        for (var current = folder is { } f ? Get(f) : null; current is not null; current = current.Parent is { } p ? Get(p) : null)
            path.Insert(0, current);
        return path;
    }

    public bool IsSelfOrDescendant(ObjectId candidate, ObjectId ancestor)
    {
        for (ObjectId? current = candidate; current is { } c; current = Get(c)?.Parent)
            if (c == ancestor) return true;
        return false;
    }

    internal static VaultSnapshot Build(Dictionary<ObjectId, EntryRecord> entries, HashSet<ObjectId> blobs)
    {
        var live = new Dictionary<ObjectId, EntryRecord>();
        foreach (var e in entries.Values)
            if (!e.Deleted) live[e.Id] = e;

        var parents = new Dictionary<ObjectId, ObjectId?>(live.Count);
        foreach (var e in live.Values) parents[e.Id] = NearestLiveFolder(e.Parent, live, entries);

        // Break cycles deterministically: visit in id order, detach the smallest member of any loop found.
        foreach (var id in parents.Keys.Order().ToList())
        {
            var seen = new Dictionary<ObjectId, int>();
            var path = new List<ObjectId>();
            for (ObjectId? current = id; current is { } c; current = parents[c])
            {
                if (seen.TryGetValue(c, out int start))
                {
                    parents[path.Skip(start).Min()] = null;
                    break;
                }
                seen[c] = path.Count;
                path.Add(c);
            }
        }

        var items = live.Values
            .Select(e => new VaultItem(e, parents[e.Id], e.Kind == EntryKind.Folder || (e.Blob is { } b && blobs.Contains(b))))
            .ToList();
        return new VaultSnapshot(items);
    }

    private static ObjectId? NearestLiveFolder(ObjectId? parent, Dictionary<ObjectId, EntryRecord> live, Dictionary<ObjectId, EntryRecord> all)
    {
        for (int hops = 0; parent is { } p && hops < 10_000; hops++)
        {
            if (live.TryGetValue(p, out var folder)) return folder.Kind == EntryKind.Folder ? p : null;
            if (!all.TryGetValue(p, out var deleted)) return null; // parent's record hasn't synced yet
            parent = deleted.Parent;                              // deleted folder: climb to its parent
        }
        return null;
    }
}
