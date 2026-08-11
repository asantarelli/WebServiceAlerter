using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebServiceAlerter.Alerting;
using WebServiceAlerter.Configuration;
using WebServiceAlerter.Data;
using WebServiceAlerter.Monitoring;
using WebServiceAlerter.Probes;
using WebServiceAlerter.Status;

namespace WebServiceAlerter;

/// <summary>
/// The polling loop. Each endpoint keeps its own schedule (interval plus jitter) and the loop
/// wakes once a second to run whatever is due, so a slow endpoint never delays a fast one.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly ProbeRunner _runner;
    private readonly CanaryChecker _canary;
    private readonly AlertDispatcher _dispatcher;
    private readonly DataRecorder _recorder;
    private readonly StatusWriter _statusWriter;
    private readonly MonitoringOptions _monitoring;
    private readonly ILogger<Worker> _logger;

    private readonly Dictionary<string, EndpointTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _nextDue = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random = new();

    private IReadOnlyList<ResolvedEndpoint> _endpoints = [];

    public Worker(
        ProbeRunner runner,
        CanaryChecker canary,
        AlertDispatcher dispatcher,
        DataRecorder recorder,
        StatusWriter statusWriter,
        IOptions<MonitoringOptions> monitoring,
        ILogger<Worker> logger)
    {
        _runner = runner;
        _canary = canary;
        _dispatcher = dispatcher;
        _recorder = recorder;
        _statusWriter = statusWriter;
        _monitoring = monitoring.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _endpoints = _runner.ResolveEndpoints();

        if (_endpoints.Count == 0)
        {
            _logger.LogError("No hay endpoints configurados. Revisá Monitoring:Endpoints.");
            return;
        }

        foreach (var endpoint in _endpoints)
        {
            _trackers[endpoint.Id] = new EndpointTracker(endpoint, _monitoring);
            _nextDue[endpoint.Id] = DateTimeOffset.UtcNow;
            _logger.LogInformation(
                "Monitoreando {Name} ({Type}) cada {Interval}s — {Url}",
                endpoint.Name, endpoint.Type, endpoint.IntervalSeconds, endpoint.Url);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            do
            {
                await RunDueChecksAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task RunDueChecksAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var due = _endpoints.Where(e => _nextDue[e.Id] <= now).ToList();

        if (due.Count == 0)
        {
            return;
        }

        foreach (var endpoint in due)
        {
            _nextDue[endpoint.Id] = now.AddSeconds(NextInterval(endpoint));
        }

        var online = await _canary.IsOnlineAsync(cancellationToken);

        // No internet: record the gap and stop there. Judging a remote service by a check we
        // could not perform is exactly the false alarm this program must never produce.
        var results = online
            ? await Task.WhenAll(due.Select(e => _runner.CheckAsync(e, cancellationToken)))
            : due.Select(NotVerifiable).ToArray();

        var transitions = new List<Transition>();

        foreach (var result in results)
        {
            _recorder.RecordSample(result);

            var tracker = _trackers[result.EndpointId];
            var previousState = tracker.State;
            var transition = tracker.Process(result, online, DateTimeOffset.UtcNow);

            if (transition.Kind != TransitionKind.None)
            {
                transitions.Add(transition);
            }

            RecordIncident(transition);
            LogStateChange(tracker, previousState, result);
        }

        await _dispatcher.DispatchAsync(transitions, online, cancellationToken);
        _statusWriter.Write(_trackers.Values.ToList(), _endpoints.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase), online);
    }

    private void RecordIncident(Transition transition)
    {
        switch (transition.Kind)
        {
            case TransitionKind.WentDown:
                _recorder.RecordIncidentStart(
                    transition.Endpoint.Id,
                    transition.DownSince ?? DateTimeOffset.UtcNow,
                    transition.Result.Outcome,
                    transition.Result.Detail);
                break;

            case TransitionKind.Recovered:
                _recorder.RecordIncidentResolved(transition.Endpoint.Id, DateTimeOffset.UtcNow);
                break;
        }
    }

    private void LogStateChange(EndpointTracker tracker, EndpointState previous, ProbeResult result)
    {
        if (tracker.State == previous)
        {
            return;
        }

        var endpoint = _endpoints.First(e => e.Id == result.EndpointId);
        var message = "{Name}: {Previous} -> {State} ({Reason})";

        if (tracker.State == EndpointState.Down)
        {
            _logger.LogWarning(message, endpoint.Name, previous, tracker.State, result.Outcome.ToSpanish());
        }
        else
        {
            _logger.LogInformation(message, endpoint.Name, previous, tracker.State, result.Outcome.ToSpanish());
        }
    }

    private static ProbeResult NotVerifiable(ResolvedEndpoint endpoint) => new()
    {
        EndpointId = endpoint.Id,
        Outcome = ProbeOutcome.NotVerifiable,
        Detail = "sin conexión a internet",
        TimestampUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Interval with jitter applied. Every client installation runs the same defaults against the
    /// same public services; without a spread they would all knock on the same second.
    /// </summary>
    private double NextInterval(ResolvedEndpoint endpoint)
    {
        var jitter = Math.Clamp(_monitoring.JitterPercent, 0, 90) / 100.0;
        if (jitter <= 0)
        {
            return endpoint.IntervalSeconds;
        }

        var factor = 1 + (_random.NextDouble() * 2 - 1) * jitter;
        return Math.Max(1, endpoint.IntervalSeconds * factor);
    }
}
