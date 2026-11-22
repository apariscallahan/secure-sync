using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SecureSync.Core.Sync;

/// <summary>
/// Finds other devices on the same chain on the local network with small UDP datagrams, sent to the
/// broadcast address, each interface's subnet broadcast, and a site-local multicast group:
///
///   "SSDA" | version | type (1 announce, 2 reply) | discovery tag (16) | device id (16) | tcp port (uint16)
///
/// The tag identifies the chain but reveals nothing about the password. Device names are only exchanged
/// after the encrypted handshake.
/// </summary>
internal sealed class Discovery : IDisposable
{
    private static ReadOnlySpan<byte> Magic => "SSDA"u8;
    private const byte Version = 1;
    private const int PacketSize = 4 + 1 + 1 + 16 + 16 + 2;
    private const int SioUdpConnReset = -1744830452;

    private readonly UdpClient _udp;
    private readonly int _port;
    private readonly IPAddress _group;
    private readonly byte[] _tag;
    private readonly byte[] _deviceId;
    private readonly int _tcpPort;

    public Discovery(int port, IPAddress group, byte[] tag, byte[] deviceId, int tcpPort)
    {
        _port = port;
        _group = group;
        _tag = tag;
        _deviceId = deviceId;
        _tcpPort = tcpPort;

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ExclusiveAddressUse = false,
            EnableBroadcast = true,
        };
        // Several SecureSync windows on one PC (or another app) may share the port.
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try { socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null); } catch (Exception) { /* not supported off Windows */ }
        socket.Bind(new IPEndPoint(IPAddress.Any, port));
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
        foreach (var address in LocalIPv4().Select(a => a.Address).Append(IPAddress.Any).Distinct())
        {
            try { socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, address)); }
            catch (SocketException) { /* interface without multicast, or already joined */ }
        }
        _udp = new UdpClient { Client = socket };
    }

    /// <summary>(sender address, device id, tcp port, isReply)</summary>
    public event Action<IPAddress, byte[], int, bool>? PeerSeen;

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            var p = result.Buffer;
            if (p.Length != PacketSize || !p.AsSpan(0, 4).SequenceEqual(Magic) || p[4] != Version) continue;
            if (!p.AsSpan(6, 16).SequenceEqual(_tag)) continue;
            var deviceId = p[22..38];
            if (deviceId.AsSpan().SequenceEqual(_deviceId)) continue;
            int tcpPort = BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(38));
            PeerSeen?.Invoke(result.RemoteEndPoint.Address, deviceId, tcpPort, p[5] == 2);
        }
    }

    public void Announce(bool reply)
    {
        var packet = new byte[PacketSize];
        Magic.CopyTo(packet);
        packet[4] = Version;
        packet[5] = reply ? (byte)2 : (byte)1;
        _tag.CopyTo(packet, 6);
        _deviceId.CopyTo(packet, 22);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(38), (ushort)_tcpPort);

        var targets = new HashSet<IPAddress> { IPAddress.Broadcast, _group };
        foreach (var (address, mask) in LocalIPv4())
        {
            var a = address.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var b = new byte[4];
            for (int i = 0; i < 4; i++) b[i] = (byte)(a[i] | ~m[i]);
            targets.Add(new IPAddress(b));
        }
        foreach (var target in targets)
        {
            try { _udp.Send(packet, packet.Length, new IPEndPoint(target, _port)); }
            catch (SocketException) { /* interface down, no route, etc. */ }
        }
    }

    /// <summary>IPv4 addresses of interfaces that are up (excluding loopback), with their subnet masks.</summary>
    public static List<(IPAddress Address, IPAddress Mask)> LocalIPv4()
    {
        var list = new List<(IPAddress, IPAddress)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.IPv4Mask is null || ua.IPv4Mask.Equals(IPAddress.Any)) continue;
                    list.Add((ua.Address, ua.IPv4Mask));
                }
            }
        }
        catch (Exception) { /* not available on some platforms (e.g. restricted on Android); broadcast still works */ }
        return list;
    }

    public void Dispose() => _udp.Dispose();
}
