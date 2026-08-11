using WebServiceAlerter.Configuration;
using WebServiceAlerter.Probes;

namespace WebServiceAlerter.Monitoring;

public enum EndpointState
{
    /// <summary>Never checked yet.</summary>
    Unknown,

    Ok,

    /// <summary>Answering, but slower than its budget.</summary>
    Degraded,

    Down,

    /// <summary>No internet: not checked, not judged, not alerted.</summary>
    Offline,

    /// <summary>Inside a declared maintenance window: still measured and charted, never alerted.</summary>
    Maintenance,
}

public enum TransitionKind
{
    None,
    WentDown,
    Recovered,
    StillDown,
}

public sealed record Transition(
    TransitionKind Kind,
    ResolvedEndpoint Endpoint,
    ProbeResult Result,
    DateTimeOffset? DownSince,
    TimeSpan? Duration);

/// <summary>
/// Per-endpoint state machine, counting consecutive results rather than elapsed time.
///
/// Counting attempts (not seconds) is the one real departure from ResourceAlerter's tracker: with
/// jitter and per-endpoint intervals, "it has been failing for 60 seconds" does not tell you
/// whether that was one attempt or ten, and the whole point of the confirmation window is to
/// require several independent attempts before waking anybody up.
/// </summary>
public sealed class EndpointTracker
{
    private readonly ResolvedEndpoint _endpoint;
    private readonly MonitoringOptions _options;

    private int _consecutiveFailures;
    private int _consecutiveSuccesses;
    private DateTimeOffset? _downSince;
    private DateTimeOffset? _lastReminderAt;

    public EndpointTracker(ResolvedEndpoint endpoint, MonitoringOptions options)
    {
        _endpoint = endpoint;
        _options = options;
    }

    public EndpointState State { get; private set; } = EndpointState.Unknown;

    public ProbeResult? LastResult { get; private set; }

    public DateTimeOffset? StateSince { get; private set; }

    public DateTimeOffset? DownSince => _downSince;

    /// <summary>
    /// Feeds one probe result in and reports whether it warrants telling anyone.
    /// </summary>
    /// <param name="online">
    /// Canary verdict. When false the result is recorded for the chart but the state machine is
    /// frozen: no counters move, no alert fires, and a local outage cannot manufacture a fake
    /// remote incident.
    /// </param>
    public Transition Process(ProbeResult result, bool online, DateTimeOffset now)
    {
        LastResult = result;

        if (!online)
        {
            SetState(EndpointState.Offline, now);
            return new Transition(TransitionKind.None, _endpoint, result, _downSince, null);
        }

        if (_endpoint.IsInMaintenance(now.ToLocalTime().DateTime))
        {
            SetState(EndpointState.Maintenance, now);
            return new Transition(TransitionKind.None, _endpoint, result, _downSince, null);
        }

        return result.IsUp ? HandleUp(result, now) : HandleDown(result, now);
    }

    private Transition HandleUp(ProbeResult result, DateTimeOffset now)
    {
        _consecutiveFailures = 0;
        _consecutiveSuccesses++;

        var wasDown = State == EndpointState.Down;
        var target = result.Outcome == ProbeOutcome.Slow ? EndpointState.Degraded : EndpointState.Ok;

        if (wasDown)
        {
            if (_consecutiveSuccesses < _options.SuccessesToRecover)
            {
                // Not confirmed yet: hold the down state so a single lucky response does not
                // announce a recovery that is not real.
                return new Transition(TransitionKind.None, _endpoint, result, _downSince, null);
            }

            var duration = _downSince is null ? (TimeSpan?)null : now - _downSince.Value;
            var since = _downSince;

            SetState(target, now);
            _downSince = null;
            _lastReminderAt = null;

            return new Transition(TransitionKind.Recovered, _endpoint, result, since, duration);
        }

        SetState(target, now);
        return new Transition(TransitionKind.None, _endpoint, result, null, null);
    }

    private Transition HandleDown(ProbeResult result, DateTimeOffset now)
    {
        _consecutiveSuccesses = 0;
        _consecutiveFailures++;

        if (State == EndpointState.Down)
        {
            if (_lastReminderAt is { } last &&
                now - last >= TimeSpan.FromMinutes(_options.ReminderIntervalMinutes))
            {
                _lastReminderAt = now;
                return new Transition(TransitionKind.StillDown, _endpoint, result, _downSince,
                    _downSince is null ? null : now - _downSince.Value);
            }

            return new Transition(TransitionKind.None, _endpoint, result, _downSince, null);
        }

        if (_consecutiveFailures < _options.FailuresToAlert)
        {
            // Still a blip as far as we know. Nothing is announced until it has failed on its own
            // several times in a row.
            return new Transition(TransitionKind.None, _endpoint, result, null, null);
        }

        SetState(EndpointState.Down, now);
        _downSince = now;
        _lastReminderAt = now;

        return new Transition(TransitionKind.WentDown, _endpoint, result, now, TimeSpan.Zero);
    }

    private void SetState(EndpointState state, DateTimeOffset now)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateSince = now;
    }
}
