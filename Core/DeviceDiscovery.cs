using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Timer = System.Threading.Timer;

namespace LocalSendWinForms.Core;

/// <summary>
/// Discovers other LocalSend devices on the LAN using two complementary
/// mechanisms, exactly like the reference client:
///   1. UDP multicast announcements on 224.0.0.167:53317 (primary, near-instant).
///   2. Active HTTP probing of the local /24 subnets against GET /v2/info
///      (fallback for networks that block multicast).
/// This type also periodically announces our own presence so peers can find us.
/// </summary>
public sealed class DeviceDiscovery : IDisposable
{
    private readonly Func<DeviceInfo> _localInfoProvider;
    private readonly bool _useHttps;
    private readonly int _port;

    private readonly ConcurrentDictionary<string, DiscoveredDevice> _devices = new();
    private readonly SemaphoreSlim _scanGate = new(32);

    private Socket? _multicastListener;
    private UdpClient? _announcer;
    private HttpClient? _http;
    private CancellationTokenSource? _cts;
    private Timer? _announceTimer;
    private Timer? _scanTimer;
    private Timer? _pruneTimer;
    private string _selfFingerprint = string.Empty;

    public event Action<DiscoveredDevice>? DeviceAdded;
    public event Action<DiscoveredDevice>? DeviceUpdated;
    public event Action<DiscoveredDevice>? DeviceRemoved;

    public DeviceDiscovery(Func<DeviceInfo> localInfoProvider, bool useHttps, int port)
    {
        _localInfoProvider = localInfoProvider;
        _useHttps = useHttps;
        _port = port;
    }

    public IReadOnlyList<DiscoveredDevice> Devices =>
        _devices.Values.OrderBy(d => d.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();

    public int Count => _devices.Count;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _selfFingerprint = CertificateManager.NormalizeFingerprint(_localInfoProvider().Fingerprint);

        _http = new HttpClient(new HttpClientHandler
        {
            // Discovery learns the peer's fingerprint from the response itself,
            // so the certificate cannot be pinned until it has been received.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect = false,
        })
        {
            Timeout = TimeSpan.FromMilliseconds(1500),
        };

        StartMulticastListener();
        StartAnnouncer();

        _announceTimer = new Timer(_ => SafeAnnounce(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));
        _scanTimer = new Timer(async _ => await ScanSubnetsAsync().ConfigureAwait(false),
            null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        _pruneTimer = new Timer(_ => Prune(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    // ---------------------------------------------------------------- UDP mDNS-style

    private void StartMulticastListener()
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, _port));
            socket.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.AddMembership,
                new MulticastOption(IPAddress.Parse(LocalSendProtocol.MulticastAddress), IPAddress.Any));

            _multicastListener = socket;
            _ = Task.Run(() => ReceiveLoopAsync(socket, _cts!.Token));
        }
        catch (SocketException ex)
        {
            // Multicast unavailable (port taken, adapter without multicast, ...).
            // HTTP scanning below still discovers peers.
            Logger.Warn($"Multicast listener unavailable: {ex.Message}");
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var remote = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(
                    buffer, SocketFlags.None, remote, ct).ConfigureAwait(false);

                if (result.ReceivedBytes <= 0) continue;

                var info = LocalSendJson.Deserialize<DeviceInfo>(
                    new ReadOnlySpan<byte>(buffer, 0, result.ReceivedBytes));
                if (info is null) continue;

                Upsert(info, ((IPEndPoint)result.RemoteEndPoint).Address);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { await Task.Delay(250, ct).ConfigureAwait(false); }
            catch (Exception ex) { Logger.Warn($"Multicast receive error: {ex.Message}"); }
        }
    }

    private void StartAnnouncer()
    {
        try
        {
            _announcer = new UdpClient
            {
                EnableBroadcast = true,
                MulticastLoopback = false,
            };
            _announcer.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
        }
        catch (SocketException ex)
        {
            Logger.Warn($"Announcer unavailable: {ex.Message}");
            _announcer = null;
        }
    }

    private void SafeAnnounce()
    {
        if (_announcer is null) return;
        try
        {
            var info = _localInfoProvider();
            info.Announcement = true;
            var bytes = Encoding.UTF8.GetBytes(LocalSendJson.Serialize(info));
            var target = new IPEndPoint(IPAddress.Parse(LocalSendProtocol.MulticastAddress), _port);
            _announcer.Send(bytes, bytes.Length, target);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Announce failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------- HTTP scan

    /// <summary>Triggers an immediate subnet scan (used by the Refresh button).</summary>
    public Task ScanNowAsync() => ScanSubnetsAsync();

    private async Task ScanSubnetsAsync()
    {
        if (_http is null || _cts is null) return;
        var ct = _cts.Token;

        var targets = BuildScanTargets();
        var tasks = targets.Select(async ip =>
        {
            await _scanGate.WaitAsync(ct).ConfigureAwait(false);
            try { await ProbeAsync(ip, "https", ct).ConfigureAwait(false); }
            finally { _scanGate.Release(); }
        });

        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private IEnumerable<IPAddress> BuildScanTargets()
    {
        var self = GetLocalAddresses();
        var seen = new HashSet<string>();
        foreach (var (address, mask) in self)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) continue;
            var prefix = mask is null ? 24 : CountBits(mask.GetAddressBytes());
            if (prefix < 22) prefix = 24; // avoid scanning huge ranges

            var network = MaskedNetwork(address, prefix);
            var hostBits = 32 - prefix;
            var total = 1 << hostBits;
            if (total > 1024) total = 1024; // hard safety cap

            var baseValue = ToUint(network);
            for (var i = 1; i < total - 1; i++)
            {
                var candidate = FromUint(baseValue + (uint)i);
                if (Equals(candidate, address)) continue;
                if (seen.Add(candidate.ToString())) yield return candidate;
            }
        }
    }

    private async Task ProbeAsync(IPAddress ip, string scheme, CancellationToken ct)
    {
        try
        {
            var url = $"{scheme}://{ip}:{_port}{LocalSendProtocol.InfoPath}";
            using var response = await _http!.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var info = LocalSendJson.Deserialize<DeviceInfo>(json);
            if (info is null || string.IsNullOrEmpty(info.Alias)) return;
            if (!string.Equals(info.Protocol, scheme, StringComparison.OrdinalIgnoreCase)) return;

            Upsert(info, ip);
        }
        catch (OperationCanceledException) { }
        catch { /* unreachable host — expected for most addresses */ }
    }

    // ------------------------------------------------------------------- bookkeeping

    private void Upsert(DeviceInfo info, IPAddress address)
    {
        info.Port = info.Port <= 0 ? LocalSendProtocol.DefaultPort : info.Port;
        info.Protocol = string.IsNullOrEmpty(info.Protocol) ? (_useHttps ? "https" : "http") : info.Protocol;

        // Peers may spell their hash as raw hex or colon-separated hex. Canonicalise it
        // once, on arrival, so the device key and every later fingerprint comparison agree.
        info.Fingerprint = CertificateManager.NormalizeFingerprint(info.Fingerprint);

        // Ignore our own announcements.
        if (!string.IsNullOrEmpty(_selfFingerprint) &&
            string.Equals(info.Fingerprint, _selfFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (address.Equals(IPAddress.Loopback)) return;

        var key = string.IsNullOrEmpty(info.Fingerprint) ? address.ToString() : info.Fingerprint;
        var device = new DiscoveredDevice { Info = info, Address = address, LastSeenUtc = DateTime.UtcNow };

        if (_devices.TryGetValue(key, out var existing))
        {
            device.Info = info;
            _devices[key] = device;
            DeviceUpdated?.Invoke(device);
        }
        else
        {
            _devices[key] = device;
            Logger.Info($"Device discovered: {device.DisplayName} @ {address}");
            DeviceAdded?.Invoke(device);
        }
    }

    private void Prune()
    {
        var cutoff = DateTime.UtcNow - LocalSendProtocol.DeviceTimeout;
        foreach (var (key, device) in _devices)
        {
            if (device.LastSeenUtc < cutoff && _devices.TryRemove(key, out var removed))
            {
                Logger.Info($"Device removed: {removed.DisplayName}");
                DeviceRemoved?.Invoke(removed);
            }
        }
    }

    // ------------------------------------------------------------------- helpers

    public static IReadOnlyList<(IPAddress Address, IPAddress? Mask)> GetLocalAddresses()
    {
        var result = new List<(IPAddress, IPAddress?)>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var uni in nic.GetIPProperties().UnicastAddresses)
            {
                if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                result.Add((uni.Address, uni.IPv4Mask));
            }
        }
        return result;
    }

    private static int CountBits(byte[] bytes)
    {
        var count = 0;
        foreach (var b in bytes)
        {
            var v = b;
            while (v != 0) { count += v & 1; v >>= 1; }
        }
        return count;
    }

    private static IPAddress MaskedNetwork(IPAddress address, int prefix)
    {
        var value = ToUint(address);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return FromUint(value & mask);
    }

    private static uint ToUint(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static IPAddress FromUint(uint value) => new(new[]
    {
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value,
    });

    public void Dispose()
    {
        _cts?.Cancel();
        _announceTimer?.Dispose();
        _scanTimer?.Dispose();
        _pruneTimer?.Dispose();

        try { _multicastListener?.Close(); } catch { }
        try { _announcer?.Dispose(); } catch { }
        try { _http?.Dispose(); } catch { }
        _scanGate.Dispose();
        _cts?.Dispose();
    }
}
