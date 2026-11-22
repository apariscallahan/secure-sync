using System.Net;
using SecureSync.Core.Crypto;
using SecureSync.Core.Storage;
using SecureSync.Core.Sync;

namespace SecureSync.Tests;

/// <summary>
/// Two or three devices on this machine, talking over real UDP discovery and TCP.
/// </summary>
public class SyncTests
{
    private sealed class Device : IAsyncDisposable
    {
        public required Vault Vault { get; init; }
        public required SyncEngine Engine { get; set; }
        public required SyncOptions Options { get; init; }
        public List<SyncNotice> Notices { get; } = [];
        public SyncStatus Status => Engine.GetStatus();
        public HashSet<string> Paths => Vault.Paths();

        public async Task RestartEngineAsync()
        {
            await Engine.DisposeAsync();
            Engine = new SyncEngine(Vault, Options);
            Engine.Start();
        }

        public async ValueTask DisposeAsync()
        {
            await Engine.DisposeAsync();
            Vault.Dispose();
        }
    }

    private static Device Start(string folder, ChainKey key, string name, int discoveryPort)
    {
        var vault = Vault.Open(folder, key, name);
        var options = new SyncOptions
        {
            DiscoveryPort = discoveryPort,
            PreferredTcpPort = 0,
            AnnounceInterval = TimeSpan.FromMilliseconds(300),
            IdleAnnounceInterval = TimeSpan.FromSeconds(1),
        };
        var device = new Device { Vault = vault, Engine = new SyncEngine(vault, options), Options = options };
        device.Engine.Notice += (_, n) => { lock (device.Notices) device.Notices.Add(n); };
        device.Engine.Start();
        return device;
    }

    private static string Describe(params Device[] devices) => string.Join(" | ", devices.Select(d =>
    {
        var s = d.Status;
        return $"{d.Vault.DeviceName}: {s.State} peers=[{string.Join(",", s.Peers.Select(p => p.DeviceName + (p.IsSynced ? "✓" : "…")))}] " +
               $"files={d.Paths.Count} missing={d.Vault.GetMissingBlobs().Count} progress={s.Progress} problems={s.Problems.Count}";
    }));

    private static int RandomPort() => Random.Shared.Next(40000, 60000);

    private static bool Synced(params Device[] devices) =>
        devices.All(d => d.Status is { State: SyncState.Synced, Progress.IsActive: false } && d.Vault.GetMissingBlobs().Count == 0)
        && devices.Skip(1).All(d => d.Paths.SetEquals(devices[0].Paths));

    [Fact]
    public async Task Pressing_sync_on_one_device_syncs_both_ways_and_stays_live()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        int port = RandomPort();
        await using var a = Start(tmp.Sub("a"), key, "PC-A", port);
        await using var b = Start(tmp.Sub("b"), key, "PC-B", port);

        var big = a.Vault.CreateFolder(null, "Big");
        a.Vault.AddFile(tmp.WriteFile("one.txt", "1"), null);
        a.Vault.AddFile(tmp.WriteFile("two.txt", new string('x', 3_000_000)), big.Id);
        var fromB = b.Vault.CreateFolder(null, "From B");
        b.Vault.AddFile(tmp.WriteFile("three.txt", "3"), fromB.Id);
        b.Vault.CreateFolder(null, "Empty");

        Assert.Equal(SyncState.Off, a.Status.State);
        a.Engine.SetMode(SyncMode.Active); // only A presses Sync
        await Wait.UntilAsync(() => Synced(a, b), "initial sync");

        Assert.Equal(new HashSet<string> { "one.txt", "Big/", "Big/two.txt", "From B/", "From B/three.txt", "Empty/" }, b.Paths);
        Assert.Equal(3_000_000, b.Vault.ReadText("Big/two.txt").Length);
        Assert.Equal("PC-B", Assert.Single(a.Status.Peers).DeviceName);

        // Live changes while connected: new folder, upload, rename, move, delete.
        var live = b.Vault.CreateFolder(null, "Live");
        var four = b.Vault.AddFile(tmp.WriteFile("four.txt", "4"), live.Id);
        a.Vault.Rename(a.Vault.Find("one.txt").Id, "uno.txt");
        await Wait.UntilAsync(() => a.Paths.Contains("Live/four.txt") && b.Paths.Contains("uno.txt"), "live create/rename");

        b.Vault.Move([four.Id], null);
        a.Vault.Delete([a.Vault.Find("Big").Id]);
        await Wait.UntilAsync(() => a.Paths.Contains("four.txt") && !b.Paths.Contains("Big/") && Synced(a, b), "live move/delete");
        Assert.Equal("4", a.Vault.ReadText("four.txt"));
    }

    [Fact]
    public async Task Changes_made_while_apart_merge_when_they_reconnect()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        int port = RandomPort();
        await using var a = Start(tmp.Sub("a"), key, "PC-A", port);
        await using var b = Start(tmp.Sub("b"), key, "PC-B", port);
        var docs = a.Vault.CreateFolder(null, "Docs");
        a.Vault.AddFile(tmp.WriteFile("report.txt", "r"), docs.Id);
        a.Vault.AddFile(tmp.WriteFile("old.txt", "o"), null);
        a.Engine.SetMode(SyncMode.Active);
        await Wait.UntilAsync(() => Synced(a, b), "first sync");

        // Apart: B stops syncing, both keep working.
        b.Engine.SetMode(SyncMode.Paused);
        await Wait.UntilAsync(() => a.Status.Peers.Count == 0, "disconnect");

        a.Vault.Rename(a.Vault.Find("Docs/report.txt").Id, "report-final.txt");
        a.Vault.AddFile(tmp.WriteFile("from-a.txt", "made on A offline"), null);
        b.Vault.Delete([b.Vault.Find("old.txt").Id]);
        var newFolder = b.Vault.CreateFolder(null, "Made on B");
        b.Vault.AddFile(tmp.WriteFile("from-b.txt", new string('b', 500_000)), newFolder.Id);
        b.Vault.Move([b.Vault.Find("Docs").Id], newFolder.Id);

        // Together again: everything from both sides arrives without anyone doing anything else.
        b.Engine.SetMode(SyncMode.Passive);
        await Wait.UntilAsync(() => Synced(a, b), "merge after reconnect");
        Assert.Equal(new HashSet<string>
        {
            "Made on B/", "Made on B/from-b.txt", "Made on B/Docs/", "Made on B/Docs/report-final.txt", "from-a.txt",
        }, a.Paths);
        Assert.Equal("made on A offline", b.Vault.ReadText("from-a.txt"));
    }

    [Fact]
    public async Task Large_files_flow_both_ways_at_the_same_time_with_progress()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        int port = RandomPort();
        await using var a = Start(tmp.Sub("a"), key, "PC-A", port);
        await using var b = Start(tmp.Sub("b"), key, "PC-B", port);
        var big = new string('a', 6_000_000);
        for (int i = 0; i < 4; i++)
        {
            a.Vault.AddFile(tmp.WriteFile($"a{i}.bin", big), null);
            b.Vault.AddFile(tmp.WriteFile($"b{i}.bin", big), null);
        }

        var fractions = new List<double>();
        a.Engine.SetMode(SyncMode.Active);
        await Wait.UntilAsync(() =>
        {
            var p = a.Status.Progress;
            if (p.IsActive) fractions.Add(p.Fraction);
            return Synced(a, b);
        }, "bidirectional sync", 60_000);
        Assert.Equal(8, a.Paths.Count);
        Assert.Contains(fractions, f => f is > 0 and < 1); // progress was visible part-way through
    }

    [Fact]
    public async Task Damaged_files_are_downloaded_again_automatically()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        int port = RandomPort();
        await using var a = Start(tmp.Sub("a"), key, "PC-A", port);
        await using var b = Start(tmp.Sub("b"), key, "PC-B", port);
        var file = a.Vault.AddFile(tmp.WriteFile("precious.txt", new string('p', 100_000)), null);
        a.Engine.SetMode(SyncMode.Active);
        await Wait.UntilAsync(() => Synced(a, b), "first sync");

        // Corrupt B's copy on disk; "Check and repair" finds it and the engine fetches a good copy from A.
        var blobPath = Path.Combine(tmp.Path, "b", "objects", "blobs", file.Blob + ".ssblob");
        var bytes = File.ReadAllBytes(blobPath);
        bytes[500] ^= 0xFF;
        File.WriteAllBytes(blobPath, bytes);
        Assert.Equal(1, b.Vault.VerifyAll().Damaged);

        await Wait.UntilAsync(() => Synced(a, b) && b.Vault.Snapshot().Get(file.Id)!.IsAvailable, "repair");
        Assert.Equal(new string('p', 100_000), b.Vault.ReadText("precious.txt"));
    }

    [Fact]
    public async Task Interrupted_transfers_resume_after_reconnecting()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        int port = RandomPort();
        await using var a = Start(tmp.Sub("a"), key, "PC-A", port);
        await using var b = Start(tmp.Sub("b"), key, "PC-B", port);
        for (int i = 0; i < 6; i++) a.Vault.AddFile(tmp.WriteFile($"f{i}.bin", new string((char)('a' + i), 4_000_000)), null);

        a.Engine.SetMode(SyncMode.Active);
        await Wait.UntilAsync(() => b.Vault.Snapshot().Items.Count(i => i.IsAvailable) is > 0 and < 6, "transfer under way");
        await b.RestartEngineAsync(); // like the network dropping mid-transfer
        await Wait.UntilAsync(() => Synced(a, b), "resume", 60_000);
        Assert.All(b.Vault.Snapshot().Items, i => Assert.True(i.IsAvailable));
    }

    [Fact]
    public async Task Other_chains_on_the_network_are_ignored()
    {
        using var tmp = new TempDir();
        int port = RandomPort();
        using var keyA = ChainKey.Generate();
        using var keyB = ChainKey.Generate();
        await using var a = Start(tmp.Sub("a"), keyA, "PC-A", port);
        await using var b = Start(tmp.Sub("b"), keyB, "PC-B", port);
        a.Engine.SetMode(SyncMode.Active);
        b.Engine.SetMode(SyncMode.Active);
        await Task.Delay(1500);
        Assert.Equal(SyncState.Searching, a.Status.State);
        Assert.Empty(a.Notices);
        Assert.Empty(b.Notices);
    }

    [Fact]
    public async Task Paused_device_refuses_connections()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        int port = RandomPort();
        await using var a = Start(tmp.Sub("a"), key, "PC-A", port);
        await using var b = Start(tmp.Sub("b"), key, "PC-B", port);
        b.Engine.SetMode(SyncMode.Paused);
        a.Engine.SetMode(SyncMode.Active);
        await Task.Delay(1500);
        Assert.Empty(a.Status.Peers);

        b.Engine.SetMode(SyncMode.Active);
        await Wait.UntilAsync(() => Synced(a, b) && a.Status.Peers.Count == 1, "sync after resume");
    }

    [Fact]
    public async Task Manual_connect_works_without_discovery()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        await using var a = Start(tmp.Sub("a"), key, "PC-A", RandomPort());
        await using var b = Start(tmp.Sub("b"), key, "PC-B", RandomPort());
        b.Vault.AddFile(tmp.WriteFile("b.txt", "b"), null);

        await a.Engine.ConnectManuallyAsync(new IPEndPoint(IPAddress.Loopback, b.Engine.ListenPort));
        await Wait.UntilAsync(() => Synced(a, b) && a.Paths.Contains("b.txt"), "manual sync");

        using var otherKey = ChainKey.Generate();
        await using var c = Start(tmp.Sub("c"), otherKey, "PC-C", RandomPort());
        var e = await Assert.ThrowsAsync<ProtocolException>(() =>
            c.Engine.ConnectManuallyAsync(new IPEndPoint(IPAddress.Loopback, b.Engine.ListenPort)));
        Assert.Contains("different sync chain", e.Message);
    }

    [Fact]
    public async Task A_device_that_was_off_catches_up_from_whichever_device_is_on()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        int port = RandomPort();
        await using var laptop = Start(tmp.Sub("laptop"), key, "Laptop", port);
        await using var phone = Start(tmp.Sub("phone"), key, "Phone", port);
        laptop.Engine.SetMode(SyncMode.Active);
        phone.Engine.SetMode(SyncMode.Active);

        // Desktop is off: the upload only reaches the phone.
        var docs = laptop.Vault.CreateFolder(null, "Docs");
        laptop.Vault.AddFile(tmp.WriteFile("report.pdf", new string('r', 400_000)), docs.Id);
        await Wait.UntilAsync(() => Synced(laptop, phone) && phone.Paths.Contains("Docs/report.pdf"), "laptop -> phone");

        // Laptop goes off, desktop comes on: it gets everything from the phone.
        await laptop.Engine.DisposeAsync();
        await using var desktop = Start(tmp.Sub("desktop"), key, "Desktop", port);
        desktop.Engine.SetMode(SyncMode.Active);
        await Wait.UntilAsync(() => Synced(phone, desktop) && desktop.Paths.Contains("Docs/report.pdf"), "phone -> desktop",
            describe: () => Describe(phone, desktop));
        Assert.Equal(400_000, desktop.Vault.ReadText("Docs/report.pdf").Length);

        // All three on: an upload anywhere reaches everyone straight away.
        await laptop.RestartEngineAsync();
        laptop.Engine.SetMode(SyncMode.Active);
        await Wait.UntilAsync(() => Synced(laptop, phone, desktop) && laptop.Status.Peers.Count == 2, "all three connected",
            describe: () => Describe(laptop, phone, desktop));
        desktop.Vault.AddFile(tmp.WriteFile("photo.jpg", "jpeg"), null);
        await Wait.UntilAsync(() => Synced(laptop, phone, desktop) && laptop.Paths.Contains("photo.jpg") && phone.Paths.Contains("photo.jpg"),
            "upload reaches everyone", describe: () => Describe(laptop, phone, desktop));
    }

    [Fact]
    public async Task Idle_connections_stay_quiet()
    {
        // Low power: once synced, no inventories or transfers keep flowing, only the occasional keep-alive.
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        int port = RandomPort();
        await using var a = Start(tmp.Sub("a"), key, "PC-A", port);
        await using var b = Start(tmp.Sub("b"), key, "PC-B", port);
        for (int i = 0; i < 20; i++) a.Vault.AddFile(tmp.WriteFile($"f{i}.txt", new string('x', 50_000)), null);
        a.Engine.SetMode(SyncMode.Active);
        await Wait.UntilAsync(() => Synced(a, b), "sync");

        var before = a.Status.Progress.BytesDone + b.Status.Progress.BytesDone;
        await Task.Delay(3000);
        Assert.Equal(SyncState.Synced, a.Status.State);
        Assert.False(a.Status.Progress.IsActive);
        Assert.Equal(0, a.Status.Progress.BytesDone + b.Status.Progress.BytesDone - before);
    }

    [Fact]
    public async Task A_remembered_address_reconnects_without_discovery()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        // Different discovery ports: they can only meet by address, like on a network that blocks discovery.
        await using var a = Start(tmp.Sub("a"), key, "PC-A", RandomPort());
        await using var b = Start(tmp.Sub("b"), key, "Phone", RandomPort());
        IPEndPoint? remembered = null;
        a.Engine.ManualPeerConnected += (_, e) => remembered = e;
        var address = new IPEndPoint(IPAddress.Loopback, b.Engine.ListenPort);
        await a.Engine.ConnectManuallyAsync(address);
        await Wait.UntilAsync(() => Synced(a, b) && remembered is not null, "manual connect");

        // Restart A (like reopening the app) with the remembered address: it reconnects by itself.
        await a.Engine.DisposeAsync();
        await Wait.UntilAsync(() => b.Status.Peers.Count == 0, "disconnect");
        a.Engine = new SyncEngine(a.Vault, a.Options with { RememberedPeers = [remembered!] });
        a.Engine.Start();
        a.Engine.SetMode(SyncMode.Active);
        b.Vault.AddFile(tmp.WriteFile("while-apart.txt", "x"), null);
        await Wait.UntilAsync(() => Synced(a, b) && a.Paths.Contains("while-apart.txt"), "automatic reconnect");
    }

    [Fact]
    public async Task Three_devices_converge()
    {
        using var tmp = new TempDir();
        using var key = ChainKey.Generate();
        int port = RandomPort();
        await using var a = Start(tmp.Sub("a"), key, "PC-A", port);
        await using var b = Start(tmp.Sub("b"), key, "PC-B", port);
        await using var c = Start(tmp.Sub("c"), key, "PC-C", port);
        a.Vault.AddFile(tmp.WriteFile("a.txt", "a"), null);
        b.Vault.AddFile(tmp.WriteFile("b.txt", "b"), null);
        c.Vault.CreateFolder(null, "c");
        a.Engine.SetMode(SyncMode.Active);

        // Devices that never pressed Sync may only be connected through A, which relays changes between them.
        await Wait.UntilAsync(() => Synced(a, b, c) && a.Paths.Count == 3, "three-way sync", describe: () => Describe(a, b, c));
        c.Vault.AddFile(tmp.WriteFile("c2.txt", "c2"), c.Vault.Find("c").Id);
        await Wait.UntilAsync(() => Synced(a, b, c) && b.Paths.Contains("c/c2.txt"), "relayed live upload", describe: () => Describe(a, b, c));
    }
}
