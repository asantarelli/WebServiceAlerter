using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebServiceAlerter.Configuration;

namespace WebServiceAlerter.Monitoring;

/// <summary>
/// Answers one question: does this machine have internet right now?
///
/// It is a gate, not a monitor. Nothing here ever raises an alert or opens an incident — its only
/// job is to stop the program from blaming a remote service for the client's own dead link. That
/// is the single most damaging false positive this kind of tool can produce, because it fires at
/// exactly the moment everyone is looking at the screen.
/// </summary>
public sealed class CanaryChecker
{
    private readonly CanaryOptions _options;
    private readonly ILogger<CanaryChecker> _logger;
    private readonly HttpClient _client = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private bool _lastVerdict = true;
    private DateTimeOffset _lastCheckedAt = DateTimeOffset.MinValue;

    public CanaryChecker(IOptions<MonitoringOptions> monitoring, ILogger<CanaryChecker> logger)
    {
        _options = monitoring.Value.Canary;
        _logger = logger;
        _client.Timeout = TimeSpan.FromMilliseconds(_options.TimeoutMs);
    }

    public async Task<bool> IsOnlineAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return true;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (DateTimeOffset.UtcNow - _lastCheckedAt < TimeSpan.FromSeconds(_options.CacheSeconds))
            {
                return _lastVerdict;
            }

            _lastVerdict = await ProbeTargetsAsync(cancellationToken);
            _lastCheckedAt = DateTimeOffset.UtcNow;
            return _lastVerdict;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Any single reachable target is enough — we are testing for "there is a working
    /// path out of here", not for a healthy network.</summary>
    private async Task<bool> ProbeTargetsAsync(CancellationToken cancellationToken)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            return false;
        }

        foreach (var target in _options.Targets)
        {
            try
            {
                if (await IsReachableAsync(target, cancellationToken))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Canario: falló el objetivo {Target}.", target);
            }
        }

        return false;
    }

    private async Task<bool> IsReachableAsync(string target, CancellationToken cancellationToken)
    {
        if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, target);
            using var response = await _client.SendAsync(request, cancellationToken);
            return true;   // any answer at all proves the path works
        }

        var host = target.Equals("gateway", StringComparison.OrdinalIgnoreCase) ? FindDefaultGateway() : target;
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        using var ping = new Ping();
        var reply = await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(_options.TimeoutMs), cancellationToken: cancellationToken);
        return reply.Status == IPStatus.Success;
    }

    private static string? FindDefaultGateway() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up &&
                          nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(nic => nic.GetIPProperties().GatewayAddresses)
            .Select(gateway => gateway.Address?.ToString())
            .FirstOrDefault(address => !string.IsNullOrWhiteSpace(address) && address != "0.0.0.0");
}
