using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using SecureSync.Core.Storage;

namespace SecureSync.Core.Sync;

/// <summary>
/// One authenticated connection to another device. Both sides run the same protocol:
///
///   Info          device id + name
///   Inventory     (entry id, version stamp) for every file and folder — the peer asks for anything newer
///   WantEntries   request entry records; answered with Entry (or EntryMissing)
///   Entry         an encrypted file/folder record: a reply, or pushed straight away after a local change
///   WantBlobs     request file contents (decided by the engine's fetcher); answered with BlobBegin/Data…/End or BlobMissing
///   Status        "I processed N of your Inventory/Entry messages and am still waiting for M things from you"
///   Digest        a 32-byte fingerprint of everything this side has, sent every 10 minutes; if the peer's differs,
///                 both exchange full inventories (InventoryRequest), so anything missed is noticed and repaired
///
/// Records and contents travel exactly as stored (already encrypted with the file key), inside the encrypted
/// channel, and are fully authenticated before they're accepted.
///
/// Threading: the receive loop never waits on the network. Everything outgoing goes through one outbox drained by a
/// single writer; file data is throttled with a few slots so it can't flood the outbox. (If the receive loop could
/// block on a send, two devices sending large files to each other at once would each stop reading and deadlock.)
/// </summary>
internal sealed class PeerConnection
{
    private const int DataChunk = 64 * 1024;
    private const int DataSlots = 16;
    private const int InventoryItemSize = ObjectId.Size + Stamp.Size;
    private const int MaxItemsPerFrame = 20_000;
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan DigestInterval = TimeSpan.FromMinutes(10);

    private readonly SyncEngine _engine;
    private readonly Vault _vault;
    private readonly TcpClient _client;
    private readonly SecureChannel _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Channel<OutgoingMessage> _outbox = Channel.CreateUnbounded<OutgoingMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _dataSlots = new(DataSlots, DataSlots);
    private readonly Channel<ObjectId> _blobsToSend = Channel.CreateUnbounded<ObjectId>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _gate = new();

    private readonly HashSet<ObjectId> _requestedEntries = [];
    private readonly Dictionary<ObjectId, long> _requestedBlobs = [];
    private int _outgoingBlobs;
    private long _outgoingBytes;
    private int _peerWaiting = -1;
    private int _unacked;
    private bool _inventoryReceived;
    private long _lastReceived = Environment.TickCount64;

    private ObjectId _incomingId;
    private FileStream? _incomingFile;
    private string? _incomingPath;
    private string? _incomingError;
    private bool _incomingActive;
    private long _incomingExpected;
    private long _incomingReceived;

    private readonly record struct OutgoingMessage(MessageType Type, byte[] Body, bool IsData);
    private sealed record InfoMessage(string DeviceId, string DeviceName, int Protocol);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public PeerConnection(SyncEngine engine, Vault vault, TcpClient client, SecureChannel channel, IPEndPoint remote,
        CancellationToken engineToken)
    {
        _engine = engine;
        _vault = vault;
        _client = client;
        _channel = channel;
        RemoteEndPoint = remote;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(engineToken);
        _cts.Token.Register(() => _client.Dispose());
    }

    public string RemoteDeviceId { get; private set; } = "";
    public string RemoteName { get; private set; } = "";
    public IPEndPoint RemoteEndPoint { get; }

    /// <summary>Both inventories exchanged and nothing outstanding in either direction on this connection.</summary>
    public bool IsSynced
    {
        get
        {
            lock (_gate)
                return _inventoryReceived && _requestedEntries.Count == 0 && _requestedBlobs.Count == 0 && !_incomingActive
                       && _peerWaiting == 0 && Volatile.Read(ref _unacked) == 0 && Volatile.Read(ref _outgoingBlobs) == 0;
        }
    }

    public bool InventoryReceived { get { lock (_gate) return _inventoryReceived; } }
    public int OutgoingBlobs => Volatile.Read(ref _outgoingBlobs);
    public long OutgoingBytes => Math.Max(0, Volatile.Read(ref _outgoingBytes));
    public int AssignedBlobs { get { lock (_gate) return _requestedBlobs.Count; } }

    /// <summary>The file contents currently arriving, if any, with bytes received so far.</summary>
    public (ObjectId Blob, long Received, long Expected)? CurrentIncoming
    {
        get { lock (_gate) return _incomingActive ? (_incomingId, _incomingReceived, _incomingExpected) : null; }
    }

    public void Close() => _cts.Cancel();

    public async Task RunAsync()
    {
        var ct = _cts.Token;
        bool registered = false;
        var writer = Task.Run(() => WriterLoopAsync(ct), ct);
        try
        {
            Post(MessageType.Info, JsonSerializer.SerializeToUtf8Bytes(new InfoMessage(_engine.DeviceId, _vault.DeviceName, 2), Json));

            using (var infoTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                infoTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                var (type, body) = await _channel.ReceiveAsync(infoTimeout.Token).ConfigureAwait(false);
                if (type != MessageType.Info) throw new ProtocolException("Expected device info.");
                var info = JsonSerializer.Deserialize<InfoMessage>(body, Json) ?? throw new ProtocolException("Bad device info.");
                if (info.DeviceId.Length != 32) throw new ProtocolException("Bad device id.");
                if (info.Protocol != 2) throw new ProtocolException("The other computer runs an incompatible version of SecureSync.");
                RemoteDeviceId = info.DeviceId.ToLowerInvariant();
                RemoteName = string.IsNullOrWhiteSpace(info.DeviceName) ? RemoteEndPoint.Address.ToString() : info.DeviceName.Trim();
                if (RemoteName.Length > 64) RemoteName = RemoteName[..64];
            }

            registered = _engine.Register(this);
            if (!registered) return;

            _ = Task.Run(() => BlobSenderLoopAsync(ct), ct);
            _ = Task.Run(() => TimerLoopAsync(ct), ct);
            SendInventory();
            await ReceiveLoopAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ProtocolException
                                      or ObjectDisposedException or JsonException or EndOfStreamException)
        {
            if (e is ProtocolException { Message: var message } && message.Contains("incompatible"))
                _engine.RaiseNotice(message, isError: true);
            // Otherwise the connection just ended; the engine reconnects when the devices see each other again.
        }
        finally
        {
            _cts.Cancel();
            _outbox.Writer.TryComplete();
            _blobsToSend.Writer.TryComplete();
            try { await writer.ConfigureAwait(false); } catch (Exception) { /* already closing */ }
            AbortIncoming();
            if (registered) _engine.Unregister(this);
            _channel.Dispose();
            _client.Dispose();
        }
    }

    // ---- Called by the engine --------------------------------------------------------------------

    /// <summary>Pushes a changed record (local change, or relayed from another device). Never blocks.</summary>
    public void Announce(byte[] entryBytes)
    {
        Interlocked.Increment(ref _unacked);
        Post(MessageType.Entry, entryBytes);
    }

    /// <summary>Asks this peer for file contents. The engine's fetcher decides which peer gets which request.</summary>
    public void RequestBlobs(IReadOnlyList<(ObjectId Blob, long Size)> blobs)
    {
        if (blobs.Count == 0) return;
        lock (_gate)
            foreach (var (blob, size) in blobs) _requestedBlobs[blob] = size;
        for (int offset = 0; offset < blobs.Count; offset += MaxItemsPerFrame)
        {
            int count = Math.Min(MaxItemsPerFrame, blobs.Count - offset);
            var body = new byte[4 + count * ObjectId.Size];
            BinaryPrimitives.WriteInt32LittleEndian(body, count);
            for (int i = 0; i < count; i++) blobs[offset + i].Blob.Write(body.AsSpan(4 + i * ObjectId.Size));
            Post(MessageType.WantBlobs, body);
        }
        SendStatus(acks: 0);
    }

    /// <summary>Full comparison in both directions ("Retry now", or when the periodic digests differ).</summary>
    public void Resync()
    {
        SendInventory();
        Post(MessageType.InventoryRequest, []);
    }

    // ---- Outgoing --------------------------------------------------------------------------------

    private void Post(MessageType type, byte[] body) => _outbox.Writer.TryWrite(new OutgoingMessage(type, body, IsData: false));

    private async Task PostDataAsync(MessageType type, byte[] body, CancellationToken ct)
    {
        await _dataSlots.WaitAsync(ct).ConfigureAwait(false);
        if (!_outbox.Writer.TryWrite(new OutgoingMessage(type, body, IsData: true))) _dataSlots.Release();
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await _channel.SendAsync(message.Type, message.Body, ct).ConfigureAwait(false);
                if (!message.IsData) continue;
                _dataSlots.Release();
                if (message.Type == MessageType.BlobData)
                {
                    long sent = message.Body.Length - ObjectId.Size;
                    Interlocked.Add(ref _outgoingBytes, -sent);
                    _engine.AddTransferredBytes(sent);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            Close();
        }
    }

    private void SendInventory()
    {
        var inventory = _vault.GetEntryInventory();
        int offset = 0;
        do
        {
            int count = Math.Min(MaxItemsPerFrame, inventory.Count - offset);
            var body = new byte[5 + count * InventoryItemSize];
            body[0] = offset + count >= inventory.Count ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), count);
            for (int i = 0; i < count; i++)
            {
                var (id, stamp) = inventory[offset + i];
                var slot = body.AsSpan(5 + i * InventoryItemSize, InventoryItemSize);
                id.Write(slot);
                stamp.Write(slot[ObjectId.Size..]);
            }
            Interlocked.Increment(ref _unacked);
            Post(MessageType.Inventory, body);
            offset += count;
        } while (offset < inventory.Count);
    }

    private void SendStatus(int acks)
    {
        var body = new byte[8];
        int waiting;
        lock (_gate) waiting = _requestedEntries.Count + _requestedBlobs.Count;
        BinaryPrimitives.WriteInt32LittleEndian(body, acks);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), waiting);
        Post(MessageType.Status, body);
    }

    /// <summary>Streams requested file contents, one at a time, exactly as stored.</summary>
    private async Task BlobSenderLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var blob in _blobsToSend.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var idBytes = new byte[ObjectId.Size];
                blob.Write(idBytes);
                try
                {
                    using var file = _vault.OpenBlob(blob);
                    if (file is null)
                    {
                        Post(MessageType.BlobMissing, idBytes);
                        continue;
                    }
                    long remaining = file.Length;
                    var begin = new byte[ObjectId.Size + 8];
                    idBytes.CopyTo(begin, 0);
                    BinaryPrimitives.WriteInt64LittleEndian(begin.AsSpan(ObjectId.Size), remaining);
                    await PostDataAsync(MessageType.BlobBegin, begin, ct).ConfigureAwait(false);
                    while (remaining > 0)
                    {
                        var chunk = new byte[ObjectId.Size + (int)Math.Min(DataChunk, remaining)];
                        idBytes.CopyTo(chunk, 0);
                        await file.ReadExactlyAsync(chunk.AsMemory(ObjectId.Size), ct).ConfigureAwait(false);
                        await PostDataAsync(MessageType.BlobData, chunk, ct).ConfigureAwait(false);
                        remaining -= chunk.Length - ObjectId.Size;
                    }
                    await PostDataAsync(MessageType.BlobEnd, idBytes, ct).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _outgoingBlobs);
                    _engine.NotifyChanged();
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or ChannelClosedException)
        {
            Close();
        }
    }

    private async Task TimerLoopAsync(CancellationToken ct)
    {
        long lastDigest = Environment.TickCount64;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(PingInterval, ct).ConfigureAwait(false);
                if (Environment.TickCount64 - Volatile.Read(ref _lastReceived) > IdleTimeout.TotalMilliseconds)
                {
                    Close();
                    return;
                }
                Post(MessageType.Ping, []);
                if (Environment.TickCount64 - lastDigest > DigestInterval.TotalMilliseconds)
                {
                    lastDigest = Environment.TickCount64;
                    Post(MessageType.Digest, _vault.InventoryDigest());
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---- Incoming --------------------------------------------------------------------------------

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            var (type, body) = await _channel.ReceiveAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref _lastReceived, Environment.TickCount64);
            switch (type)
            {
                case MessageType.Inventory: HandleInventory(body); break;
                case MessageType.WantEntries: HandleWantEntries(body); break;
                case MessageType.Entry: HandleEntry(body); break;
                case MessageType.EntryMissing: HandleEntryMissing(body); break;
                case MessageType.WantBlobs: HandleWantBlobs(body); break;
                case MessageType.BlobBegin: HandleBlobBegin(body); break;
                case MessageType.BlobData: HandleBlobData(body); break;
                case MessageType.BlobEnd: HandleBlobEnd(body); break;
                case MessageType.BlobMissing: HandleBlobMissing(body); break;
                case MessageType.Status: HandleStatus(body); break;
                case MessageType.Ping: Post(MessageType.Pong, []); break;
                case MessageType.Digest:
                    if (!body.AsSpan().SequenceEqual(_vault.InventoryDigest())) Resync();
                    break;
                case MessageType.InventoryRequest: SendInventory(); break;
                case MessageType.Pong: break;
                default: break; // newer protocol message; ignore
            }
            _engine.NotifyChanged();
        }
    }

    private void HandleInventory(byte[] body)
    {
        if (body.Length < 5) throw new ProtocolException("Bad inventory.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1));
        if (count < 0 || body.Length != 5 + (long)count * InventoryItemSize) throw new ProtocolException("Bad inventory.");
        var wants = new List<ObjectId>();
        lock (_gate)
        {
            for (int i = 0; i < count; i++)
            {
                var slot = body.AsSpan(5 + i * InventoryItemSize, InventoryItemSize);
                var id = ObjectId.Read(slot);
                if (!_requestedEntries.Contains(id) && _vault.WantsEntry(id, Stamp.Read(slot[ObjectId.Size..])))
                {
                    _requestedEntries.Add(id);
                    wants.Add(id);
                }
            }
            if (body[0] == 1) _inventoryReceived = true;
        }
        if (body[0] == 1) _engine.ScheduleFetch(); // this peer can now be asked for missing contents
        for (int offset = 0; offset < wants.Count; offset += MaxItemsPerFrame)
        {
            int n = Math.Min(MaxItemsPerFrame, wants.Count - offset);
            var request = new byte[4 + n * ObjectId.Size];
            BinaryPrimitives.WriteInt32LittleEndian(request, n);
            for (int i = 0; i < n; i++) wants[offset + i].Write(request.AsSpan(4 + i * ObjectId.Size));
            Post(MessageType.WantEntries, request);
        }
        SendStatus(acks: 1);
    }

    private void HandleWantEntries(byte[] body)
    {
        foreach (var id in ReadIdList(body))
        {
            var bytes = _vault.ReadEntryBytes(id);
            if (bytes is null)
            {
                var idBytes = new byte[ObjectId.Size];
                id.Write(idBytes);
                Post(MessageType.EntryMissing, idBytes);
                continue;
            }
            Interlocked.Increment(ref _unacked);
            Post(MessageType.Entry, bytes);
        }
    }

    private void HandleEntry(byte[] body)
    {
        // The record's id is in its (authenticated) header; read it to clear the request even if applying fails.
        if (body.Length >= 32)
            lock (_gate) _requestedEntries.Remove(ObjectId.Read(body.AsSpan(16, ObjectId.Size)));
        try
        {
            _vault.ApplyEntry(body, this);
        }
        catch (Exception e) when (e is ObjectCorruptException or IOException)
        {
            _engine.RaiseNotice($"A change received from {RemoteName} couldn't be saved ({e.Message}). It will be retried automatically.", isError: true);
        }
        SendStatus(acks: 1);
    }

    private void HandleEntryMissing(byte[] body)
    {
        if (body.Length != ObjectId.Size) throw new ProtocolException("Bad message.");
        lock (_gate) _requestedEntries.Remove(ObjectId.Read(body));
        SendStatus(acks: 0);
    }

    private void HandleWantBlobs(byte[] body)
    {
        foreach (var blob in ReadIdList(body))
        {
            Interlocked.Increment(ref _outgoingBlobs);
            Interlocked.Add(ref _outgoingBytes, _vault.BlobFileSize(blob));
            _blobsToSend.Writer.TryWrite(blob);
        }
    }

    private void HandleBlobBegin(byte[] body)
    {
        if (body.Length != ObjectId.Size + 8) throw new ProtocolException("Bad file header.");
        long size = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(ObjectId.Size));
        if (size < 0) throw new ProtocolException("Bad file size.");
        lock (_gate)
        {
            if (_incomingActive) throw new ProtocolException("Overlapping transfers.");
            _incomingId = ObjectId.Read(body);
            _incomingExpected = size;
            _incomingReceived = 0;
            _incomingActive = true;
            _incomingError = null;
            _incomingPath = _vault.NewIncomingPath();
            try
            {
                _incomingFile = new FileStream(_incomingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
            }
            catch (IOException e)
            {
                _incomingError = e.Message;
            }
        }
    }

    private void HandleBlobData(byte[] body)
    {
        if (body.Length < ObjectId.Size || !_incomingActive || ObjectId.Read(body) != _incomingId)
            throw new ProtocolException("Unexpected file data.");
        int length = body.Length - ObjectId.Size;
        if (_incomingReceived + length > _incomingExpected) throw new ProtocolException("File larger than announced.");
        lock (_gate) _incomingReceived += length;
        _engine.AddTransferredBytes(length);
        if (_incomingFile is null) return; // a write already failed; drain the rest and report at the end
        try
        {
            _incomingFile.Write(body, ObjectId.Size, length);
        }
        catch (IOException e)
        {
            // e.g. disk full: keep the connection, discard this file, and let the fetcher retry later.
            _incomingError = e.Message;
            _incomingFile.Dispose();
            _incomingFile = null;
        }
    }

    private void HandleBlobEnd(byte[] body)
    {
        if (body.Length != ObjectId.Size || !_incomingActive || ObjectId.Read(body) != _incomingId)
            throw new ProtocolException("Unexpected end of file.");
        if (_incomingReceived != _incomingExpected) throw new ProtocolException("File shorter than announced.");

        var blob = _incomingId;
        var path = _incomingPath!;
        string? error = _incomingError;
        if (_incomingFile is not null)
        {
            try
            {
                _incomingFile.Flush(flushToDisk: true);
            }
            catch (IOException e)
            {
                error = e.Message;
            }
            _incomingFile.Dispose();
            _incomingFile = null;
        }
        if (error is null)
        {
            try
            {
                _vault.ImportBlob(path, blob, this);
            }
            catch (ObjectCorruptException)
            {
                error = "the copy received was damaged in transit";
            }
            catch (IOException e)
            {
                error = e.Message;
            }
        }
        try { File.Delete(path); } catch (IOException) { }

        lock (_gate)
        {
            _incomingActive = false;
            _incomingPath = null;
            _requestedBlobs.Remove(blob);
        }
        if (error is null) _engine.OnBlobReceived(this, blob);
        else _engine.OnBlobFailed(this, blob, error);
        SendStatus(acks: 0);
    }

    private void HandleBlobMissing(byte[] body)
    {
        if (body.Length != ObjectId.Size) throw new ProtocolException("Bad message.");
        var blob = ObjectId.Read(body);
        lock (_gate) _requestedBlobs.Remove(blob);
        _engine.OnBlobMissing(this, blob);
        SendStatus(acks: 0);
    }

    private void HandleStatus(byte[] body)
    {
        if (body.Length != 8) throw new ProtocolException("Bad status.");
        int acks = BinaryPrimitives.ReadInt32LittleEndian(body);
        int waiting = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4));
        if (acks < 0 || waiting < 0) throw new ProtocolException("Bad status.");
        if (Interlocked.Add(ref _unacked, -acks) < 0) Interlocked.Exchange(ref _unacked, 0);
        lock (_gate) _peerWaiting = waiting;
    }

    private static IEnumerable<ObjectId> ReadIdList(byte[] body)
    {
        if (body.Length < 4) throw new ProtocolException("Bad request.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(body);
        if (count < 0 || body.Length != 4 + (long)count * ObjectId.Size) throw new ProtocolException("Bad request.");
        var ids = new ObjectId[count];
        for (int i = 0; i < count; i++) ids[i] = ObjectId.Read(body.AsSpan(4 + i * ObjectId.Size));
        return ids;
    }

    /// <summary>Requests this connection still held when it closed, so the engine can hand them to another peer.</summary>
    public List<ObjectId> TakeAssignedBlobs()
    {
        lock (_gate)
        {
            var list = _requestedBlobs.Keys.ToList();
            _requestedBlobs.Clear();
            return list;
        }
    }

    private void AbortIncoming()
    {
        lock (_gate)
        {
            _incomingFile?.Dispose();
            _incomingFile = null;
            if (_incomingPath is not null)
                try { File.Delete(_incomingPath); } catch (IOException) { }
            _incomingPath = null;
            _incomingActive = false;
        }
    }
}
