using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NetHog.Models;

namespace NetHog.Services;

/// <summary>
/// Shares temporary peer-device controls over the local IPv4 LAN. The controller
/// advertises active rules; the controlled device can send a direct release command
/// back without requiring approval from the controller.
/// </summary>
public sealed class RemoteControlCoordinator
{
    public const int CoordinationPort = 39721;

    private const int ProtocolVersion = 1;
    private static readonly TimeSpan AdvertisementInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RemoteLeaseTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ReleaseRetryDelay = TimeSpan.FromMilliseconds(160);
    private readonly object _sync = new();
    private readonly RemoteControlFirewallService _firewallService = new();
    private readonly Dictionary<string, RemoteControlLease> _localLeases = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RemoteControlLease> _remoteLeases = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _controllerId = Guid.NewGuid().ToString("N");
    private readonly string _controllerName = Environment.MachineName;
    private UdpClient? _socket;
    private CancellationTokenSource? _cancellation;
    private Task? _receiveTask;
    private Task? _advertisementTask;
    private Task? _expiryTask;
    private string? _localMacAddress;
    private string? _localIpAddress;
    private IPAddress _broadcastAddress = IPAddress.Broadcast;
    private bool _started;

    public event Action<IReadOnlyList<RemoteControlLease>>? ActiveRemoteControlsChanged;
    public event Action<RemoteControlReleaseRequest>? ReleaseRequested;

    public IReadOnlyList<RemoteControlLease> GetActiveRemoteControls()
    {
        lock (_sync) return _remoteLeases.Values.OrderBy(lease => lease.ControllerName).ToArray();
    }

    public void SetTargetIdentity(NetworkSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var localMac = NormalizeMac(snapshot.LocalMacAddress);
        if (localMac.Length == 0 || !IPAddress.TryParse(snapshot.LocalAddress, out var localAddress)) return;

        lock (_sync)
        {
            _localMacAddress = localMac;
            _localIpAddress = localAddress.ToString();
            _broadcastAddress = CalculateBroadcast(localAddress, snapshot.PrefixLength);
        }
    }

    public async Task StartAsync()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
        }

        UdpClient? socket = null;
        try
        {
            try { await _firewallService.EnsureAsync(); }
            catch { /* Status sharing remains best-effort if firewall policy blocks setup. */ }

            socket = new UdpClient(AddressFamily.InterNetwork);
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, CoordinationPort));
            socket.EnableBroadcast = true;

            lock (_sync)
            {
                _socket = socket;
                _cancellation = new CancellationTokenSource();
                _receiveTask = ReceiveLoopAsync(socket, _cancellation.Token);
                _advertisementTask = AdvertisementLoopAsync(_cancellation.Token);
                _expiryTask = ExpireRemoteLeasesAsync(_cancellation.Token);
            }
        }
        catch
        {
            lock (_sync) _started = false;
            try { socket?.Dispose(); }
            catch { }
            try { await _firewallService.RemoveAsync(); }
            catch { }
            throw;
        }
    }

    public async Task StopAsync()
    {
        Task? receiveTask;
        Task? advertisementTask;
        Task? expiryTask;
        CancellationTokenSource? cancellation;
        UdpClient? socket;
        lock (_sync)
        {
            if (!_started) return;
            _started = false;
            receiveTask = _receiveTask;
            advertisementTask = _advertisementTask;
            expiryTask = _expiryTask;
            cancellation = _cancellation;
            socket = _socket;
            _receiveTask = null;
            _advertisementTask = null;
            _expiryTask = null;
            _cancellation = null;
            _socket = null;
            _localLeases.Clear();
            _remoteLeases.Clear();
        }

        cancellation?.Cancel();
        try { socket?.Close(); }
        catch { }

        var tasks = new[] { receiveTask, advertisementTask, expiryTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length > 0)
        {
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }
            catch (SocketException) { }
        }

        socket?.Dispose();
        cancellation?.Dispose();
        try { await _firewallService.RemoveAsync(); }
        catch { }
        RaiseActiveRemoteControlsChanged(Array.Empty<RemoteControlLease>());
    }

    public async Task PublishControlAsync(
        NetworkDevice target,
        DeviceControlRule rule,
        string sessionId)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(rule);
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        var targetMac = NormalizeMac(target.MacAddress);
        if (targetMac.Length == 0) return;
        var now = DateTimeOffset.UtcNow;
        var lease = new RemoteControlLease(
            _controllerId,
            _controllerName,
            GetLocalAddress(),
            targetMac,
            target.IpAddress,
            rule.DownloadLimitMbps,
            rule.UploadLimitMbps,
            rule.BlockInternet,
            rule.DurationMinutes is > 0 ? now.AddMinutes(rule.DurationMinutes.Value) : null,
            sessionId,
            Guid.NewGuid().ToString("N"),
            now);

        lock (_sync)
        {
            foreach (var existingKey in _localLeases
                         .Where(pair => pair.Value.TargetMacAddress.Equals(targetMac, StringComparison.OrdinalIgnoreCase)
                                        && pair.Value.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _localLeases.Remove(existingKey);
            }

            _localLeases[lease.IdentityKey] = lease;
        }
        await SendAdvertisementSafelyAsync(CreateMessage("status", lease));
    }

    public async Task RemoveControlAsync(string targetMacAddress, string? sessionId = null)
    {
        var targetMac = NormalizeMac(targetMacAddress);
        if (targetMac.Length == 0) return;

        RemoteControlLease[] removed;
        lock (_sync)
        {
            removed = _localLeases
                .Where(pair => pair.Value.TargetMacAddress.Equals(targetMac, StringComparison.OrdinalIgnoreCase)
                               && (sessionId is null || pair.Value.SessionId.Equals(sessionId, StringComparison.OrdinalIgnoreCase)))
                .Select(pair => pair.Value)
                .ToArray();
            foreach (var lease in removed) _localLeases.Remove(lease.IdentityKey);
        }

        foreach (var lease in removed)
        {
            await SendAdvertisementSafelyAsync(CreateMessage("released", lease));
        }
    }

    public async Task RemoveAllControlsAsync()
    {
        RemoteControlLease[] removed;
        lock (_sync)
        {
            removed = _localLeases.Values.ToArray();
            _localLeases.Clear();
        }

        foreach (var lease in removed)
        {
            await SendAdvertisementSafelyAsync(CreateMessage("released", lease));
        }
    }

    public async Task<bool> RequestReleaseAsync(RemoteControlLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var requesterMac = string.Empty;
        var requesterIp = string.Empty;
        lock (_sync)
        {
            requesterMac = _localMacAddress ?? string.Empty;
            requesterIp = _localIpAddress ?? string.Empty;
        }

        var message = new WireMessage(
            ProtocolVersion,
            "release-request",
            lease.ControllerId,
            string.Empty,
            string.Empty,
            lease.TargetMacAddress,
            lease.TargetIpAddress,
            null,
            null,
            false,
            lease.ExpiresAtUtc,
            lease.SessionId,
            lease.LeaseId,
            requesterMac,
            requesterIp,
            DateTimeOffset.UtcNow);

        var destination = IPAddress.TryParse(lease.ControllerAddress, out var controllerAddress)
            ? controllerAddress
            : IPAddress.Broadcast;
        var sent = false;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sent |= await SendMessageSafelyAsync(message, destination);
            if (attempt < 2) await Task.Delay(ReleaseRetryDelay, cancellationToken);
        }

        return sent;
    }

    private async Task ReceiveLoopAsync(UdpClient socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                continue;
            }

            if (received.Buffer.Length == 0 || received.Buffer.Length > 16_384) continue;
            WireMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<WireMessage>(received.Buffer, _jsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (message is not null) HandleMessage(message);
        }
    }

    private void HandleMessage(WireMessage message)
    {
        if (message.Version != ProtocolVersion || string.IsNullOrWhiteSpace(message.MessageType)) return;
        var targetMac = NormalizeMac(message.TargetMacAddress);
        if (targetMac.Length == 0 || string.IsNullOrWhiteSpace(message.ControllerId)) return;

        if (message.MessageType.Equals("release-request", StringComparison.OrdinalIgnoreCase))
        {
            bool isOwned;
            lock (_sync)
            {
                isOwned = message.ControllerId.Equals(_controllerId, StringComparison.OrdinalIgnoreCase)
                          && _localLeases.TryGetValue(CreateLocalKey(targetMac, message.SessionId, message.LeaseId), out var ownedLease)
                          && (NormalizeMac(message.RequesterMacAddress).Equals(targetMac, StringComparison.OrdinalIgnoreCase)
                              || (!string.IsNullOrWhiteSpace(message.RequesterIpAddress)
                                  && message.RequesterIpAddress.Equals(ownedLease.TargetIpAddress, StringComparison.OrdinalIgnoreCase)));
            }

            if (isOwned)
            {
                try
                {
                    ReleaseRequested?.Invoke(new RemoteControlReleaseRequest(
                        message.ControllerId,
                        message.SessionId,
                        message.LeaseId,
                        targetMac,
                        NormalizeMac(message.RequesterMacAddress),
                        message.RequesterIpAddress));
                }
                catch
                {
                    // A UI callback must not stop LAN coordination for future rules.
                }
            }

            return;
        }

        if (message.ControllerId.Equals(_controllerId, StringComparison.OrdinalIgnoreCase)) return;
        string? localMac;
        lock (_sync) localMac = _localMacAddress;
        if (localMac is null || !localMac.Equals(targetMac, StringComparison.OrdinalIgnoreCase)) return;

        var lease = new RemoteControlLease(
            message.ControllerId,
            string.IsNullOrWhiteSpace(message.ControllerName) ? "Another NetHog instance" : message.ControllerName,
            message.ControllerAddress,
            targetMac,
            message.TargetIpAddress,
            message.DownloadLimitMbps,
            message.UploadLimitMbps,
            message.BlockInternet,
            message.ExpiresAtUtc,
            message.SessionId,
            message.LeaseId,
            DateTimeOffset.UtcNow);

        if (message.MessageType.Equals("released", StringComparison.OrdinalIgnoreCase))
        {
            var removed = false;
            lock (_sync) removed = _remoteLeases.Remove(lease.IdentityKey);
            if (removed) RaiseActiveRemoteControlsChanged(GetActiveRemoteControls());
            return;
        }

        if (!message.MessageType.Equals("status", StringComparison.OrdinalIgnoreCase)) return;
        var changed = false;
        lock (_sync)
        {
            changed = !_remoteLeases.TryGetValue(lease.IdentityKey, out var existing)
                      || !AreSameLease(existing, lease);

            _remoteLeases[lease.IdentityKey] = lease;
        }

        if (changed) RaiseActiveRemoteControlsChanged(GetActiveRemoteControls());
    }

    private async Task AdvertisementLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(AdvertisementInterval, cancellationToken);
                RemoteControlLease[] leases;
                lock (_sync) leases = _localLeases.Values.ToArray();
                foreach (var lease in leases)
                {
                    if (lease.ExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow) continue;
                    await SendAdvertisementSafelyAsync(CreateMessage("status", lease));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task ExpireRemoteLeasesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(AdvertisementInterval, cancellationToken);
                var now = DateTimeOffset.UtcNow;
                RemoteControlLease[] expired;
                lock (_sync)
                {
                    expired = _remoteLeases.Values
                        .Where(lease => lease.LastSeenUtc + RemoteLeaseTimeout <= now
                                       || lease.ExpiresAtUtc is { } expiry && expiry <= now)
                        .ToArray();
                    foreach (var lease in expired) _remoteLeases.Remove(lease.IdentityKey);
                }

                if (expired.Length > 0) RaiseActiveRemoteControlsChanged(GetActiveRemoteControls());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private WireMessage CreateMessage(string messageType, RemoteControlLease lease) =>
        new(
            ProtocolVersion,
            messageType,
            lease.ControllerId,
            lease.ControllerName,
            lease.ControllerAddress,
            lease.TargetMacAddress,
            lease.TargetIpAddress,
            lease.DownloadLimitMbps,
            lease.UploadLimitMbps,
            lease.BlockInternet,
            lease.ExpiresAtUtc,
            lease.SessionId,
            lease.LeaseId,
            string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow);

    private async Task SendAdvertisementSafelyAsync(WireMessage message)
    {
        IPAddress destination;
        lock (_sync) destination = _broadcastAddress;
        await SendMessageSafelyAsync(message, destination);
    }

    private async Task<bool> SendMessageSafelyAsync(WireMessage message, IPAddress destination)
    {
        try
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, _jsonOptions);
            using var sender = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            await sender.SendAsync(payload, payload.Length, new IPEndPoint(destination, CoordinationPort));
            return true;
        }
        catch
        {
            // Coordination is best-effort; packet control must not fail because
            // another machine cannot receive the status message.
            return false;
        }
    }

    private void RaiseActiveRemoteControlsChanged(IReadOnlyList<RemoteControlLease> leases)
    {
        try { ActiveRemoteControlsChanged?.Invoke(leases); }
        catch { /* A UI callback must not stop LAN coordination. */ }
    }

    private static bool AreSameLease(RemoteControlLease left, RemoteControlLease right) =>
        left.ControllerId.Equals(right.ControllerId, StringComparison.OrdinalIgnoreCase)
        && left.ControllerName.Equals(right.ControllerName, StringComparison.Ordinal)
        && left.ControllerAddress.Equals(right.ControllerAddress, StringComparison.OrdinalIgnoreCase)
        && left.TargetMacAddress.Equals(right.TargetMacAddress, StringComparison.OrdinalIgnoreCase)
        && left.TargetIpAddress.Equals(right.TargetIpAddress, StringComparison.OrdinalIgnoreCase)
        && left.DownloadLimitMbps == right.DownloadLimitMbps
        && left.UploadLimitMbps == right.UploadLimitMbps
        && left.BlockInternet == right.BlockInternet
        && left.ExpiresAtUtc == right.ExpiresAtUtc
        && left.SessionId.Equals(right.SessionId, StringComparison.OrdinalIgnoreCase)
        && left.LeaseId.Equals(right.LeaseId, StringComparison.OrdinalIgnoreCase);

    private string GetLocalAddress()
    {
        lock (_sync) return _localIpAddress ?? string.Empty;
    }

    private static string CreateLocalKey(string targetMacAddress, string sessionId, string leaseId) =>
        $"{targetMacAddress}:{sessionId}:{leaseId}";

    private static string NormalizeMac(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var hex = value.Replace(":", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Trim();
        return hex.Length == 12 && hex.All(Uri.IsHexDigit)
            ? string.Join(':', Enumerable.Range(0, 6).Select(index => hex.Substring(index * 2, 2).ToUpperInvariant()))
            : string.Empty;
    }

    private static IPAddress CalculateBroadcast(IPAddress localAddress, int prefixLength)
    {
        if (localAddress.AddressFamily != AddressFamily.InterNetwork || prefixLength is < 0 or > 32)
        {
            return IPAddress.Broadcast;
        }

        var address = BinaryPrimitives.ReadUInt32BigEndian(localAddress.GetAddressBytes());
        var mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var broadcast = address | ~mask;
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, broadcast);
        return new IPAddress(bytes);
    }

    private sealed record WireMessage(
        int Version,
        string MessageType,
        string ControllerId,
        string ControllerName,
        string ControllerAddress,
        string TargetMacAddress,
        string TargetIpAddress,
        int? DownloadLimitMbps,
        int? UploadLimitMbps,
        bool BlockInternet,
        DateTimeOffset? ExpiresAtUtc,
        string SessionId,
        string LeaseId,
        string RequesterMacAddress,
        string RequesterIpAddress,
        DateTimeOffset SentAtUtc);
}
