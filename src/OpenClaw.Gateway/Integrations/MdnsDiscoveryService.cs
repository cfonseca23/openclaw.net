using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Integrations;

/// <summary>
/// Advertises the OpenClaw gateway on the local network via mDNS/DNS-SD.
/// Enables automatic discovery by companion apps and nodes.
/// </summary>
internal sealed class MdnsDiscoveryService : IAsyncDisposable
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private const int MdnsPort = 5353;

    private readonly MdnsConfig _config;
    private readonly int _gatewayPort;
    private readonly ILogger<MdnsDiscoveryService> _logger;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private UdpClient? _udp;

    public MdnsDiscoveryService(MdnsConfig config, int gatewayPort, ILogger<MdnsDiscoveryService> logger)
    {
        _config = config;
        _gatewayPort = gatewayPort;
        _logger = logger;
    }

    public void Start(CancellationToken ct)
    {
        if (!_config.Enabled)
            return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
            _udp.JoinMulticastGroup(MulticastAddress);

            _listenTask = ListenLoopAsync(_cts.Token);

            var instanceName = _config.InstanceName ?? Environment.MachineName;
            var port = _config.Port > 0 ? _config.Port : _gatewayPort;
            _logger.LogInformation("mDNS discovery started: {Instance}.{ServiceType}.local on port {Port}.",
                instanceName, _config.ServiceType, port);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start mDNS discovery. Service advertisement disabled.");
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct);
                // Check if the query is for our service type
                var query = Encoding.UTF8.GetString(result.Buffer);
                if (query.Contains(_config.ServiceType, StringComparison.OrdinalIgnoreCase))
                    await SendResponseAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "mDNS receive error (non-fatal).");
            }
        }
    }

    private async Task SendResponseAsync(CancellationToken ct)
    {
        try
        {
            var instanceName = _config.InstanceName ?? Environment.MachineName;
            var port = _config.Port > 0 ? _config.Port : _gatewayPort;

            // Build a minimal DNS-SD TXT record response
            var txt = $"version=1.0\nport={port}\nauth={(!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENCLAW_AUTH_TOKEN")) ? "required" : "none")}";
            var response = BuildSimpleMdnsResponse(instanceName, _config.ServiceType, port, txt);

            var endpoint = new IPEndPoint(MulticastAddress, MdnsPort);
            await _udp!.SendAsync(response, endpoint, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send mDNS response.");
        }
    }

    /// <summary>
    /// Builds a minimal mDNS response packet. This is a simplified implementation
    /// for basic service advertisement. For full DNS-SD compliance, consider a
    /// dedicated mDNS library.
    /// </summary>
    private static byte[] BuildSimpleMdnsResponse(string instanceName, string serviceType, int port, string txt)
    {
        // Simplified: encode as a TXT record with service info
        var payload = $"{instanceName}.{serviceType}.local\tport={port}\t{txt}";
        var data = Encoding.UTF8.GetBytes(payload);

        // Wrap in a minimal DNS response packet
        var packet = new byte[12 + data.Length];
        // Transaction ID = 0 (mDNS)
        // Flags: 0x8400 (response, authoritative)
        packet[2] = 0x84;
        packet[3] = 0x00;
        // Answer count = 1
        packet[7] = 0x01;
        // Copy payload
        Buffer.BlockCopy(data, 0, packet, 12, data.Length);

        return packet;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_listenTask is not null)
        {
            try { await _listenTask; }
            catch (OperationCanceledException) { }
        }
        _udp?.Dispose();
        _cts?.Dispose();
    }
}
