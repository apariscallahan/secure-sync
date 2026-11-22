using System.Net;
using System.Net.Sockets;
using SecureSync.Core.Storage;

namespace SecureSync.Core.Sync;

public enum SyncMode
{
    /// <summary>Default after unlocking: accepts connections from devices that press Sync, but does not search itself.</summary>
    Passive,
    /// <summary>Sync pressed: searches the network and reconnects automatically.</summary>
    Active,
    /// <summary>Sync stopped by the user: no connections in either direction.</summary>
    Paused,
}

public enum SyncState { Off, Searching, Syncing, Synced }

public sealed record PeerStatus(string DeviceName, string Address, bool IsSynced);

/// <summary>Transfer progress since syncing last went idle. <see cref="Fraction"/> runs 0 → 1.</summary>
public sealed record SyncProgress(int FilesRemaining, long BytesRemaining, long BytesDone)
{
    public bool IsActive => FilesRemaining > 0 || BytesRemaining > 0;
    public double Fraction => BytesDone + BytesRemaining == 0 ? (IsActive ? 0 : 1) : (double)BytesDone / (BytesDone + BytesRemaining);
}

/// <summary>A file that couldn't be synced yet, and why. Retried automatically.</summary>
public sealed record SyncProblem(ObjectId Blob, string FileName, string Message);

public sealed record SyncStatus(SyncState State, SyncMode Mode, IReadOnlyList<PeerStatus> Peers, SyncProgress Progress,
    IReadOnlyList<SyncProblem> Problems);

public enum FileSyncState { Downloading, Queued, Waiting, Problem }

public sealed record FileSyncInfo(FileSyncState State, double Progress, string Message);

public sealed record SyncNotice(string Message, bool IsError);

public sealed record SyncOptions
{
    public int DiscoveryPort { get; init; } = 47820;
    public int PreferredTcpPort { get; init; } = 47821;
    public IPAddress MulticastGroup { get; init; } = IPAddress.Parse("239.255.73.83");
    /// <summary>Base spacing of the short burst of announcements sent when syncing starts or the network changes.</summary>
    public TimeSpan AnnounceInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>After the burst: one tiny announcement per this interval, so devices switched on later are found.</summary>
    public TimeSpan IdleAnnounceInterval { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>
    /// Addresses this device once reached with "Connect by IP address". They're tried again (quietly) whenever syncing
    /// starts or the network changes, for networks where automatic discovery is blocked.
    /// </summary>
    public IReadOnlyList<IPEndPoint> RememberedPeers { get; init; } = [];
}

/// <summary>
/// Finds devices on the same chain, keeps authenticated connections to them, relays changes, and makes sure every
/// file's contents arrive: a fetcher hands missing contents to connected peers a few at a time, and on failure
/// (damaged in transit, disk full, connection dropped, peer doesn't have it) retries with back-off or tries another
/// peer, until the file is complete.
///
/// Power use: nothing runs on a fixed fast timer. Work is triggered by events (a change, a connection, a reply); the
/// only timers are one-shot retry wake-ups when a retry is actually due, a short burst of discovery announcements when
/// syncing starts or the network changes (then one per minute), and a 30-second keep-alive per open connection.
/// </summary>
public sealed class SyncEngine : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(6);
    private static readonly double[] AnnounceBurst = [1, 1.5, 2.5, 5];
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReportUnavailableAfter = TimeSpan.FromSeconds(15);
    private const int MaxAssignedPerPeer = 8;

    private readonly Vault _vault;
    private readonly SyncOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, PeerConnection> _peers = [];
    private readonly HashSet<string> _connecting = [];
    private readonly Dictionary<string, long> _retryAfter = [];
    private readonly Dictionary<string, long> _noticeAfter = [];
    private readonly Dictionary<ObjectId, BlobFetch> _fetches = [];
    private TcpListener? _listener;
    private Discovery? _discovery;
    private CancellationTokenSource? _announceCts;
    private Timer? _wakeTimer;
    private long _lastReply;
    private long _transferred;
    private long _sessionBaseline;
    private int _fetchScheduled;
    private bool _disposed;

    private sealed class BlobFetch
    {
        public PeerConnection? Peer;
        /// <summary>Peers that said they don't have it, and when we may ask them again.</summary>
        public readonly Dictionary<string, long> NotAt = [];
        /// <summary>When every connected peer started lacking it (0 = not currently the case).</summary>
        public long UnavailableSince;
        public int Failures;
        public long RetryAt;
        public string? Failure;

        public string? Problem(long now) =>
            Peer is not null ? null
            : Failure ?? (UnavailableSince != 0 && now - UnavailableSince > ReportUnavailableAfter.TotalMilliseconds
                ? "None of the connected computers has this file yet. It will download as soon as one does."
                : null);
    }

    public SyncEngine(Vault vault, SyncOptions? options = null)
    {
        _vault = vault;
        _options = options ?? new SyncOptions();
        DeviceId = Convert.ToHexStringLower(vault.DeviceId);
    }

    public string DeviceId { get; }
    public int ListenPort { get; private set; }
    public bool DiscoveryAvailable => _discovery is not null;
    public SyncMode Mode { get; private set; } = SyncMode.Passive;

    /// <summary>Raised on a background thread whenever peers, transfers or problems change.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised on a background thread for things the user should know about.</summary>
    public event EventHandler<SyncNotice>? Notice;

    /// <summary>A "Connect by IP address" succeeded; apps save the address so it's tried again next time.</summary>
    public event EventHandler<IPEndPoint>? ManualPeerConnected;

    public void Start()
    {
        _listener = CreateListener();
        ListenPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        try
        {
            _discovery = new Discovery(_options.DiscoveryPort, _options.MulticastGroup, _vault.Keys.DiscoveryTag, _vault.DeviceId, ListenPort);
            _discovery.PeerSeen += OnPeerSeen;
            var discovery = _discovery;
            _ = Task.Run(() => discovery.RunAsync(_cts.Token));
        }
        catch (SocketException e)
        {
            RaiseNotice($"Automatic discovery is unavailable ({e.Message}). You can still connect by IP address.", isError: true);
        }
        _vault.Changed += OnVaultChanged;
        _wakeTimer = new Timer(_ => ScheduleFetch(), null, Timeout.Infinite, Timeout.Infinite);
        try { System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkChanged; }
        catch (Exception e) when (e is PlatformNotSupportedException or NotImplementedException) { /* apps can call NudgeDiscovery */ }
        ScheduleFetch();
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => NudgeDiscovery();

    /// <summary>Announce again right away (e.g. after Wi-Fi reconnects) and forget connection back-offs.</summary>
    public void NudgeDiscovery()
    {
        lock (_gate)
        {
            if (_disposed || Mode != SyncMode.Active) return;
            _retryAfter.Clear();
            StartAnnouncingLocked();
        }
    }

    /// <summary>Caller holds _gate.</summary>
    private void StartAnnouncingLocked()
    {
        _announceCts?.Cancel();
        _announceCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var token = _announceCts.Token;
        _ = Task.Run(() => AnnounceLoopAsync(token));
    }

    private TcpListener CreateListener()
    {
        foreach (var port in new[] { _options.PreferredTcpPort, 0 })
        {
            var listener = new TcpListener(IPAddress.Any, port) { ExclusiveAddressUse = true };
            try
            {
                listener.Start();
                return listener;
            }
            catch (SocketException) when (port != 0)
            {
                listener.Stop(); // port taken (e.g. a second SecureSync window); fall back to any free port
            }
        }
        throw new InvalidOperationException("unreachable");
    }

    public void SetMode(SyncMode mode)
    {
        PeerConnection[] toClose = [];
        lock (_gate)
        {
            if (_disposed || Mode == mode) return;
            Mode = mode;
            _announceCts?.Cancel();
            _announceCts = null;
            if (mode == SyncMode.Active)
            {
                _retryAfter.Clear();
                StartAnnouncingLocked();
            }
            if (mode == SyncMode.Paused) toClose = [.. _peers.Values];
        }
        foreach (var peer in toClose) peer.Close();
        NotifyChanged();
    }

    /// <summary>Retries everything now: failed downloads, and a fresh comparison with every connected computer.</summary>
    public void RetryNow()
    {
        PeerConnection[] peers;
        lock (_gate)
        {
            foreach (var fetch in _fetches.Values)
            {
                fetch.RetryAt = 0;
                fetch.NotAt.Clear();
                fetch.Failure = null;
                fetch.UnavailableSince = 0;
            }
            _retryAfter.Clear();
            peers = [.. _peers.Values];
        }
        foreach (var peer in peers) peer.Resync();
        NudgeDiscovery();
        ScheduleFetch();
        NotifyChanged();
    }

    public SyncStatus GetStatus()
    {
        var missing = _vault.GetMissingBlobs();
        PeerConnection[] peers;
        SyncMode mode;
        var problemBlobs = new List<(ObjectId Blob, string Message)>();
        int filesRemaining = 0;
        long bytesRemaining = 0, done;
        bool anyAssigned;
        lock (_gate)
        {
            peers = [.. _peers.Values];
            mode = Mode;
            long now = Environment.TickCount64;
            foreach (var (blob, size) in missing)
            {
                _fetches.TryGetValue(blob, out var fetch);
                if (fetch?.Problem(now) is { } error)
                {
                    problemBlobs.Add((blob, error));
                    continue;
                }
                if (peers.Length == 0) continue;
                filesRemaining++;
                bytesRemaining += size;
            }
            anyAssigned = _fetches.Values.Any(f => f.Peer is not null);
        }
        foreach (var peer in peers)
        {
            if (peer.CurrentIncoming is { } incoming) bytesRemaining -= incoming.Received;
            filesRemaining += peer.OutgoingBlobs;
            bytesRemaining += peer.OutgoingBytes;
        }
        bytesRemaining = Math.Max(0, bytesRemaining);
        lock (_gate)
        {
            if (filesRemaining == 0 && bytesRemaining == 0) _sessionBaseline = Interlocked.Read(ref _transferred);
            done = Math.Max(0, Interlocked.Read(ref _transferred) - _sessionBaseline);
        }

        var peerStatuses = peers
            .Select(p => new PeerStatus(p.RemoteName, p.RemoteEndPoint.Address.ToString(), p.IsSynced))
            .OrderBy(p => p.DeviceName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var state = peerStatuses.Count == 0
            ? (mode == SyncMode.Active ? SyncState.Searching : SyncState.Off)
            : peerStatuses.All(p => p.IsSynced) && !anyAssigned && filesRemaining == 0 ? SyncState.Synced : SyncState.Syncing;
        var problems = problemBlobs
            .Select(p => new SyncProblem(p.Blob, _vault.NameOfBlob(p.Blob) ?? "A file", p.Message))
            .ToList();
        return new SyncStatus(state, mode, peerStatuses, new SyncProgress(filesRemaining, bytesRemaining, done), problems);
    }

    /// <summary>Where a file that isn't on this computer yet stands.</summary>
    public FileSyncInfo GetFileState(ObjectId blob)
    {
        lock (_gate)
        {
            bool connected = _peers.Count > 0;
            if (!_fetches.TryGetValue(blob, out var fetch))
                return connected ? new(FileSyncState.Queued, 0, "Waiting to download") : new(FileSyncState.Waiting, 0, "Waiting for your other computer");
            if (fetch.Peer is { } peer)
            {
                if (peer.CurrentIncoming is { } incoming && incoming.Blob == blob)
                {
                    double progress = incoming.Expected == 0 ? 0 : (double)incoming.Received / incoming.Expected;
                    return new(FileSyncState.Downloading, progress, $"Downloading {progress:P0}");
                }
                return new(FileSyncState.Queued, 0, "Waiting to download");
            }
            if (fetch.Problem(Environment.TickCount64) is { } error) return new(FileSyncState.Problem, 0, error);
            return connected ? new(FileSyncState.Queued, 0, "Waiting to download") : new(FileSyncState.Waiting, 0, "Waiting for your other computer");
        }
    }

    /// <summary>Connects straight to a device, for networks that block discovery broadcasts. Throws with a readable message on failure.</summary>
    public async Task ConnectManuallyAsync(IPEndPoint endpoint, CancellationToken ct = default)
    {
        if (Mode != SyncMode.Active) SetMode(SyncMode.Active);
        var client = new TcpClient(endpoint.AddressFamily) { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token))
            {
                timeout.CancelAfter(ConnectTimeout);
                try
                {
                    await client.ConnectAsync(endpoint, timeout.Token);
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException)
                {
                    throw new ProtocolException($"Couldn't reach {endpoint}. Check the address, that SecureSync is unlocked there, and that Windows Firewall allows it.");
                }
            }
            var channel = await HandshakeAsync(client, initiator: true, ct);
            _ = Task.Run(() => RunPeerAsync(client, channel, endpoint));
            ManualPeerConnected?.Invoke(this, endpoint);
        }
        catch (ProtocolException e) when (e.Message.StartsWith("That device is on a different", StringComparison.Ordinal))
        {
            client.Dispose();
            throw new ProtocolException("That computer is using a different sync chain key.");
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ---- Fetcher ---------------------------------------------------------------------------------

    internal void ScheduleFetch()
    {
        if (Interlocked.Exchange(ref _fetchScheduled, 1) == 0) _ = Task.Run(FetchTick);
    }

    /// <summary>Hands missing file contents to connected peers, a few per peer at a time, respecting back-off.</summary>
    private void FetchTick()
    {
        Interlocked.Exchange(ref _fetchScheduled, 0);
        if (_disposed) return;
        List<(ObjectId Blob, long Size)> missing;
        try
        {
            missing = _vault.GetMissingBlobs();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        var batches = new Dictionary<PeerConnection, List<(ObjectId, long)>>();
        lock (_gate)
        {
            if (_disposed) return;
            var needed = missing.Select(m => m.Blob).ToHashSet();
            foreach (var gone in _fetches.Keys.Where(b => !needed.Contains(b)).ToList()) _fetches.Remove(gone);

            var peers = _peers.Values.Where(p => p.InventoryReceived).ToList();
            var load = peers.ToDictionary(p => p, p => p.AssignedBlobs);
            long now = Environment.TickCount64;
            foreach (var (blob, size) in missing)
            {
                if (!_fetches.TryGetValue(blob, out var fetch)) _fetches[blob] = fetch = new BlobFetch();
                if (fetch.Peer is not null || now < fetch.RetryAt || peers.Count == 0) continue;

                bool MayAsk(PeerConnection p) => !fetch.NotAt.TryGetValue(p.RemoteDeviceId, out var until) || now >= until;
                var peer = peers.Where(p => MayAsk(p) && load[p] < MaxAssignedPerPeer).OrderBy(p => load[p]).FirstOrDefault();
                if (peer is null)
                {
                    if (!peers.Any(MayAsk) && fetch.UnavailableSince == 0) fetch.UnavailableSince = now;
                    continue;
                }
                fetch.Peer = peer;
                load[peer]++;
                if (!batches.TryGetValue(peer, out var batch)) batches[peer] = batch = [];
                batch.Add((blob, size));
            }
        }
        foreach (var (peer, batch) in batches) peer.RequestBlobs(batch);
        ScheduleWake();
        NotifyChanged();
    }

    /// <summary>Arms a one-shot timer for the next moment a waiting download may be retried (or reported as a problem).</summary>
    private void ScheduleWake()
    {
        long now = Environment.TickCount64, next = long.MaxValue;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var fetch in _fetches.Values.Where(f => f.Peer is null))
            {
                if (fetch.RetryAt > now) next = Math.Min(next, fetch.RetryAt);
                foreach (var until in fetch.NotAt.Values)
                    if (until > now) next = Math.Min(next, until);
                if (fetch.UnavailableSince != 0)
                {
                    long report = fetch.UnavailableSince + (long)ReportUnavailableAfter.TotalMilliseconds;
                    if (report > now) next = Math.Min(next, report);
                }
            }
            _wakeTimer?.Change(next == long.MaxValue ? Timeout.Infinite : next - now + 50, Timeout.Infinite);
        }
    }

    internal void OnBlobReceived(PeerConnection peer, ObjectId blob)
    {
        lock (_gate) _fetches.Remove(blob);
        ScheduleFetch();
    }

    internal void OnBlobMissing(PeerConnection peer, ObjectId blob)
    {
        lock (_gate)
        {
            if (!_fetches.TryGetValue(blob, out var fetch)) return;
            fetch.Peer = null;
            // It may be downloading it from someone else right now; ask again shortly, and try other peers first.
            fetch.NotAt[peer.RemoteDeviceId] = Environment.TickCount64 + (long)AskAgainAfter.TotalMilliseconds;
        }
        ScheduleFetch();
    }

    internal void OnBlobFailed(PeerConnection peer, ObjectId blob, string error)
    {
        lock (_gate)
        {
            if (!_fetches.TryGetValue(blob, out var fetch)) return;
            fetch.Peer = null;
            fetch.Failures++;
            var delay = TimeSpan.FromSeconds(Math.Min(60, 1 << Math.Min(fetch.Failures, 6)));
            fetch.RetryAt = Environment.TickCount64 + (long)delay.TotalMilliseconds;
            fetch.NotAt[peer.RemoteDeviceId] = fetch.RetryAt; // prefer another peer, if there is one, next time
            fetch.Failure = $"Download from {peer.RemoteName} failed ({error}). Retrying automatically.";
        }
        ScheduleFetch();
        NotifyChanged();
    }

    internal void AddTransferredBytes(long bytes) => Interlocked.Add(ref _transferred, bytes);

    // ---- Discovery -------------------------------------------------------------------------------

    /// <summary>A few quick announcements, then one per <see cref="SyncOptions.IdleAnnounceInterval"/>.</summary>
    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        try
        {
            for (int i = 0; !ct.IsCancellationRequested; i++)
            {
                _discovery?.Announce(reply: false);
                if (i < AnnounceBurst.Length) DialRememberedPeers();
                await Task.Delay(i < AnnounceBurst.Length ? _options.AnnounceInterval * AnnounceBurst[i] : _options.IdleAnnounceInterval, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnPeerSeen(IPAddress address, byte[] deviceId, int port, bool isReply)
    {
        var id = Convert.ToHexStringLower(deviceId);
        bool connect = false, reply = false;
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (_disposed || Mode == SyncMode.Paused) return;
            if (_peers.ContainsKey(id) || _connecting.Contains(id)) return;
            if (_retryAfter.TryGetValue(id, out var after) && now < after) return;

            // Exactly one side dials: the device with the smaller id. Announcements are always answered (but replies
            // are not), so a device that has not pressed Sync can still be reached, and other listening devices
            // learn about each other too.
            if (string.CompareOrdinal(DeviceId, id) < 0)
            {
                _connecting.Add(id);
                connect = true;
            }
            if (!isReply && now - _lastReply > 500)
            {
                _lastReply = now;
                reply = true;
            }
        }
        if (connect) _ = DialAsync(new IPEndPoint(address, port), id);
        if (reply) _discovery?.Announce(reply: true);
    }

    /// <summary>Tries remembered "Connect by IP" addresses that aren't connected yet. Failures stay silent.</summary>
    private void DialRememberedPeers()
    {
        var toDial = new List<(IPEndPoint Endpoint, string Key)>();
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (_disposed || Mode != SyncMode.Active) return;
            foreach (var endpoint in _options.RememberedPeers)
            {
                var key = "addr:" + endpoint;
                if (_peers.Values.Any(p => p.RemoteEndPoint.Address.Equals(endpoint.Address)) || _connecting.Contains(key)) continue;
                if (_retryAfter.TryGetValue(key, out var after) && now < after) continue;
                _connecting.Add(key);
                toDial.Add((endpoint, key));
            }
        }
        foreach (var (endpoint, key) in toDial) _ = DialAsync(endpoint, key, quiet: true);
    }

    private async Task DialAsync(IPEndPoint endpoint, string expectedId, bool quiet = false)
    {
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                timeout.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(endpoint, timeout.Token);
            }
            var channel = await HandshakeAsync(client, initiator: true, _cts.Token);
            await RunPeerAsync(client, channel, endpoint);
        }
        catch (PeerAuthenticationException e)
        {
            Backoff(expectedId, TimeSpan.FromSeconds(30));
            RaiseNoticeThrottled(endpoint.Address.ToString(), $"{e.Message} ({endpoint.Address})");
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException or ProtocolException)
        {
            if (!_cts.IsCancellationRequested)
            {
                Backoff(expectedId, TimeSpan.FromSeconds(5));
                if (!quiet && e is SocketException or OperationCanceledException)
                    RaiseNoticeThrottled(endpoint.Address.ToString(),
                        $"Found a computer on this sync chain at {endpoint.Address} but couldn't connect. Make sure Windows Firewall allows SecureSync on both computers.");
            }
        }
        finally
        {
            client.Dispose();
            lock (_gate) _connecting.Remove(expectedId);
            NotifyChanged();
        }
    }

    private void Backoff(string key, TimeSpan delay)
    {
        lock (_gate) _retryAfter[key] = Environment.TickCount64 + (long)delay.TotalMilliseconds;
    }

    // ---- Connections -----------------------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            var remote = (IPEndPoint)client.Client.RemoteEndPoint!;
            string ipKey = "ip:" + remote.Address;
            bool reject;
            lock (_gate)
                reject = Mode == SyncMode.Paused || (_retryAfter.TryGetValue(ipKey, out var after) && Environment.TickCount64 < after);
            if (reject)
            {
                client.Dispose();
                continue;
            }
            client.NoDelay = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    var channel = await HandshakeAsync(client, initiator: false, ct);
                    await RunPeerAsync(client, channel, remote);
                }
                catch (PeerAuthenticationException e)
                {
                    Backoff(ipKey, TimeSpan.FromSeconds(3)); // slows down guessing
                    RaiseNoticeThrottled(remote.Address.ToString(), $"{e.Message} ({remote.Address})");
                }
                catch (Exception e) when (e is SocketException or OperationCanceledException or IOException or ProtocolException) { }
                finally
                {
                    client.Dispose();
                }
            }, ct);
        }
    }

    private async Task<SecureChannel> HandshakeAsync(TcpClient client, bool initiator, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        timeout.CancelAfter(HandshakeTimeout);
        return await Handshake.RunAsync(client.GetStream(), _vault.Keys, initiator, timeout.Token);
    }

    private async Task RunPeerAsync(TcpClient client, SecureChannel channel, IPEndPoint remote)
    {
        var peer = new PeerConnection(this, _vault, client, channel, remote, _cts.Token);
        await peer.RunAsync();
    }

    internal bool Register(PeerConnection peer)
    {
        lock (_gate)
        {
            if (_disposed || Mode == SyncMode.Paused) return false;
            if (peer.RemoteDeviceId == DeviceId) return false; // connected to ourselves
            if (_peers.ContainsKey(peer.RemoteDeviceId)) return false; // already connected another way
            _peers[peer.RemoteDeviceId] = peer;
        }
        NotifyChanged();
        return true;
    }

    internal void Unregister(PeerConnection peer)
    {
        lock (_gate)
        {
            if (_peers.TryGetValue(peer.RemoteDeviceId, out var current) && ReferenceEquals(current, peer))
                _peers.Remove(peer.RemoteDeviceId);
            // Whatever this peer was fetching goes back in the pool for another peer (or the next connection).
            peer.TakeAssignedBlobs();
            foreach (var fetch in _fetches.Values.Where(f => ReferenceEquals(f.Peer, peer))) fetch.Peer = null;
        }
        ScheduleFetch();
        NotifyChanged();
    }

    private void OnVaultChanged(object? sender, VaultChange change)
    {
        if (change.Kind == VaultChangeKind.Entry && _vault.ReadEntryBytes(change.Id) is { } bytes)
        {
            PeerConnection[] targets;
            lock (_gate) targets = [.. _peers.Values.Where(p => !ReferenceEquals(p, change.Source))];
            foreach (var peer in targets) peer.Announce(bytes);
        }
        ScheduleFetch();
        NotifyChanged();
    }

    internal void NotifyChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    internal void RaiseNotice(string message, bool isError) => Notice?.Invoke(this, new SyncNotice(message, isError));

    private void RaiseNoticeThrottled(string key, string message)
    {
        long now = Environment.TickCount64;
        lock (_gate)
        {
            if (_noticeAfter.TryGetValue(key, out var after) && now < after) return;
            _noticeAfter[key] = now + 30_000;
        }
        RaiseNotice(message, isError: true);
    }

    public ValueTask DisposeAsync()
    {
        PeerConnection[] peers;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            peers = [.. _peers.Values];
            _peers.Clear();
        }
        _wakeTimer?.Dispose();
        try { System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkChanged; }
        catch (Exception e) when (e is PlatformNotSupportedException or NotImplementedException) { }
        _vault.Changed -= OnVaultChanged;
        _cts.Cancel();
        _listener?.Stop();
        _discovery?.Dispose();
        foreach (var peer in peers) peer.Close();
        return ValueTask.CompletedTask;
    }
}
